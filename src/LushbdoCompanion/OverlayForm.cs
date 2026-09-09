using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LushbdoCompanion;

/// <summary>
/// The overlay over the game window while a session runs: two figures (#39)
/// — what the session is worth so far, and its pace in silver per hour — and
/// (#52) up to three item slots, each the icon and running count of an item
/// the member put in a slot on the site. Nothing else on it — no log, no
/// list, no controls. One glance that saves an alt-tab.
///
/// Two windows, one mind. This is the figures' own window and the one that
/// decides everything: it finds the game, follows it, knows whether a run is
/// live and whether the last reply is stale, and owns the slots' window
/// (<see cref="SlotsPane"/>) as a second pane it moves and paints under the
/// same rules. What the two windows have in common — the layered-window
/// plumbing and the drag — is <see cref="LayeredPane"/>, written once; how
/// the overlay stays in the same class as everything else here is argued on
/// it. Two windows rather than one because the figures and the slots hang
/// from anchors of their own, and one bitmap spanning both would be most of
/// the game window repainted on every pickup.
///
/// The figures and the slots are levels, like the silver balance: they are
/// whatever the site last reported on an ingest reply, and the app posts
/// exactly when the value changes, so nothing polls. The site values the run
/// itself (bdo#724, shipped in bdo#725) and this draws the gross figures —
/// the full value before the market's cut, which is what the site's own
/// "Worth so far" shows unless a reader flips that view to after tax
/// (owner ruling, 2026-09-09: always before tax; #42 had picked net, and
/// the overlay and the sheet then showed two numbers for one run). On a
/// site from before it, or a sheet nothing could be priced on, the item
/// count shows in the value's place; a pace that cannot be shown yet is a
/// dash, not the clock — the clock in that spot read as strange (owner,
/// 2026-09-09). Each line has its own size and the order is the member's.
/// The slots are the site's too (bdo#728): which items, their names and
/// counts and where their icons are all arrive on the reply, and nothing
/// here chooses — a slot set on the site mid-run shows at the next reply,
/// which is the next pickup or the quiet ask, by the same no-poll ruling.
/// Three minutes without a reply and everything goes (owner, 2026-09-09):
/// the sender speaks only when there is loot to send, so a session stopped
/// with no pickup after it would otherwise never reach this window, and
/// stale figures would stand over the game for as long as the member played
/// on. The next reply brings them back. A timer, not a poll — the owner's
/// call, and it costs the site nothing. Hidden whenever there is nothing
/// honest to show — no running session, a pause, the game not in front, the
/// game gone — and repainted only when a figure changes. Following the
/// window is a position query four times a second and nothing more.
///
/// Where the figures sit is an <see cref="OverlayPlacement"/> (#43) and where
/// the slots sit a <see cref="SlotsPlacement"/> — anchor, offset and a text
/// size that is a share of the game window's height, the slots with a
/// spacing and a direction on top — resolved against the window's bounds on
/// every follow, so the same setting holds across a resolution change or a
/// resized client. While the settings window's Overlay or Item slots page is
/// open the same windows draw samples instead (<see cref="Preview"/>), so
/// the pages need no mock of their own.
///
/// That is also the one time the windows take the mouse: in preview the
/// click-through style is dropped so the samples can be dragged to where
/// they should sit, and a drop is said back as a placement — the anchor
/// snaps to the cell of the window the bitmap landed in, the offset follows
/// (<see cref="Placed"/>, <see cref="SlotsPlaced"/>). The moment the page
/// closes the style is back and the windows are click-through again; live
/// figures are never in the way of a click on the game.
/// </summary>
public sealed class OverlayForm : LayeredPane
{
    /// <summary>The figures the settings page previews with. Fixed: an editable sample is a control nobody uses twice.</summary>
    public const string SampleValue = "12,345,678";
    public const string SamplePace = "45.6M/h";

    /// <summary>The pace's place while there is no pace to show: the line keeps its spot, and says it is waiting.</summary>
    private const string NoPace = "–";

    /// <summary>
    /// The site's pace is the value over the gathering time, whatever that
    /// time is — its own rule, and it does not extrapolate. Under a couple of
    /// minutes that figure is one lucky drop scaled to an hour, so a dash
    /// shows until the window is long enough to mean something.
    /// </summary>
    private const long PaceAfterSec = 120;

    /// <summary>Three slots, by ruling. A site that ever sent more is drawn to three.</summary>
    private const int SlotCount = 3;

    private static readonly TimeSpan FollowPace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FindPace = TimeSpan.FromSeconds(5);

    /// <summary>How long the last figures stand without another reply behind them.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly System.Windows.Forms.Timer _follow;
    private readonly PrivateFontCollection? _fonts;
    private readonly IntPtr _fontMemory;
    private readonly IconCache _icons;
    private readonly SlotsPane _slots;
    private Font _valueFont;
    private Font _paceFont;
    private float _valuePx;
    private float _pacePx;

    private OverlayPlacement _placement;
    private SlotsPlacement _slotsPlacement;
    private string _line1 = "";
    private string _line2 = "";
    private IReadOnlyList<SlotsPane.Row> _rows = [];
    private bool _live;                  // the site last reported a running session
    private bool _preview;               // the settings page is open: sample figures, game in front or not
    private bool _dirty = true;          // a figure changed since the last paint
    private bool _slotsDirty = true;     // a slot, or the slots' layout, changed since theirs
    private Size _slotsWindow;           // the game window size the slots were last laid out for: the spacing is a share of it
    private IntPtr _game;
    private DateTime _nextFind = DateTime.MinValue;
    private DateTime _heardAt = DateTime.MinValue; // the last reply that carried a session

    /// <summary>The figures were dragged somewhere in a preview, and this is where, as a placement.</summary>
    public event Action<OverlayPlacement>? Placed;

    /// <summary>The slots were dragged somewhere in a preview, and this is where, as their placement.</summary>
    public event Action<SlotsPlacement>? SlotsPlaced;

    public OverlayForm(OverlayPlacement placement, SlotsPlacement slots, IconCache icons) : base("LushBDO Companion overlay")
    {
        (_fonts, _fontMemory) = LoadTypeface();
        _placement = placement;
        _slotsPlacement = slots;
        _icons = icons;
        // Sized for nothing yet: the first follow that sees the game window
        // remakes these at the sizes the placement says for that window.
        _valuePx = _pacePx = 0;
        _valueFont = MakeFont(1);
        _paceFont = MakeFont(1);
        _slots = new SlotsPane(icons, MakeFont);

        Dropped += at =>
        {
            if (GameBounds() is { } bounds)
            {
                _placement = _placement.At(bounds, Painted, at);
                Placed?.Invoke(_placement);
            }
            Follow();
        };
        _slots.Dropped += at =>
        {
            if (GameBounds() is { } bounds)
            {
                _slotsPlacement = _slotsPlacement.At(bounds, _slots.Painted, at);
                SlotsPlaced?.Invoke(_slotsPlacement);
            }
            Follow();
        };
        _icons.Loaded += OnIconLoaded;

        _follow = new System.Windows.Forms.Timer { Interval = (int)FollowPace.TotalMilliseconds };
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
    }

    /// <summary>
    /// Where to draw the figures. Takes effect on the spot: a moved anchor or
    /// offset is one window move, a changed size is one repaint — the settings
    /// page calls this on every control change and that is all each change
    /// costs. A changed column is a repaint too, since the two lines align on
    /// the edge they hang from.
    /// </summary>
    public void Place(OverlayPlacement placement)
    {
        if (placement.Column != _placement.Column) _dirty = true;
        _placement = placement;
        Follow();
    }

    /// <summary>Where to draw the slots, likewise. Direction, spacing and column all change the bitmap, so it is one repaint.</summary>
    public void PlaceSlots(SlotsPlacement placement)
    {
        _slotsPlacement = placement;
        _slotsDirty = true;
        Follow();
    }

    /// <summary>
    /// The settings page's live preview: draw the samples over the game
    /// whether or not a session is live and whether or not the game is in
    /// front — the settings window is what is in front then — and take the
    /// mouse so they can be dragged. One repaint on the way in and one on the
    /// way out; the live figures come back exactly as they were, and
    /// click-through with them.
    /// </summary>
    public void Preview(bool on)
    {
        if (on == _preview) return;
        _preview = on;
        if (!on)
        {
            CancelDrag();
            _slots.CancelDrag();
        }
        _dirty = _slotsDirty = true;
        Follow();
    }

    /// <summary>
    /// What the site said about the session on its latest reply; null when
    /// it said the run is not live. Levels, not events: the same figures
    /// twice are nothing to repaint. Gross is the figure shown — the same
    /// one as the site's headline, before the market's cut — and a value the
    /// site could not put on the sheet gives way to the item count rather
    /// than reading as zero. A pace there is not one of yet — the run too
    /// young, or a site that sends none — is a dash in the pace's place. The
    /// slots are the reply's too, in its order, the empty ones left out.
    /// </summary>
    public void Report(IngestClient.SessionInfo? session)
    {
        var live = session is not null;
        var line1 = session switch
        {
            null => "",
            { ValueGross: { } value } => Silver(value),
            { } s => $"{s.Items} item{(s.Items == 1 ? "" : "s")}",
        };
        var line2 = session switch
        {
            null => "",
            { SilverPerHourGross: { } pace, ElapsedSec: >= PaceAfterSec } => Compact(pace) + "/h",
            _ => NoPace,
        };
        var rows = RowsOf(session);
        // Every reply with a session on it restarts the clock, the same
        // figures included: the next follow brings back what staleness hid.
        if (live) _heardAt = DateTime.UtcNow;
        var sameFigures = live == _live && line1 == _line1 && line2 == _line2;
        var sameRows = rows.SequenceEqual(_rows);
        if (sameFigures && sameRows) return;
        _live = live;
        _line1 = line1;
        _line2 = line2;
        _rows = rows;
        // The samples are what is on screen during a preview; the new figures
        // are painted when it ends. Each window repaints for its own change
        // only: a pickup that moved a slot's count leaves the figures' bitmap
        // alone unless the value moved too.
        if (!_preview)
        {
            if (!sameFigures) _dirty = true;
            if (!sameRows) _slotsDirty = true;
        }
        Follow();
    }

    /// <summary>
    /// The reply's slots as rows to draw: the first three, in slot order,
    /// filled ones only — a cleared slot closes up rather than leaving a
    /// hole in a HUD. An icon path is taken only if it is one, which is what
    /// keeps the cache from ever being asked for anything else.
    /// </summary>
    private static IReadOnlyList<SlotsPane.Row> RowsOf(IngestClient.SessionInfo? session)
    {
        if (session?.Slots is not { Count: > 0 } slots) return [];
        var rows = new List<SlotsPane.Row>(SlotCount);
        foreach (var slot in slots.Take(SlotCount))
        {
            if (slot is null) continue;
            var icon = slot.IconPath is { } path && IngestClient.IsIconPath(path) ? path : null;
            rows.Add(new SlotsPane.Row(slot.Qty.ToString("N0", Inv), icon, null));
        }
        return rows;
    }

    private void OnIconLoaded()
    {
        _slotsDirty = true;
        Follow();
    }

    private Rectangle? GameBounds()
    {
        if (_game == IntPtr.Zero) return null;
        var bounds = GameWindow.BoundsOf(_game);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    private void Follow()
    {
        if (IsDisposed) return;
        // Applied on every follow rather than only when the preview flips,
        // so nothing can leave a live overlay sitting in the way of a click.
        TakeMouse(_preview);
        _slots.TakeMouse(_preview);
        if (!(_live || _preview))
        {
            ConcealAll();
            return;
        }
        // Figures nobody has vouched for in three minutes come down; a
        // preview draws samples and has no reply to be stale against.
        if (!_preview && DateTime.UtcNow - _heardAt > StaleAfter)
        {
            ConcealAll();
            return;
        }

        // The game window is found the way the picker finds it — by process —
        // and that walk is not free, so it happens when the handle is lost and
        // then no more than every few seconds.
        var now = DateTime.UtcNow;
        if (_game == IntPtr.Zero || !IsWindow(_game))
        {
            _game = IntPtr.Zero;
            if (now < _nextFind)
            {
                ConcealAll();
                return;
            }
            _nextFind = now + FindPace;
            _game = GameWindow.Find()?.Hwnd ?? IntPtr.Zero;
            if (_game == IntPtr.Zero)
            {
                ConcealAll();
                return;
            }
        }

        // Only over the game while the game is in front: a member who tabbed
        // out to the site does not want session figures on top of it. The
        // preview is the exception, since the settings window is in front
        // precisely while the member is placing this one.
        if (!_preview && GetForegroundWindow() != _game)
        {
            ConcealAll();
            return;
        }
        var bounds = GameWindow.BoundsOf(_game);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            ConcealAll();
            return;
        }

        FollowFigures(bounds);
        FollowSlots(bounds);
    }

    private void FollowFigures(Rectangle bounds)
    {
        // The text sizes are shares of the window's height, so a resized
        // client or a changed setting is a new font; a window that only moved
        // is not.
        var valuePx = _placement.TextPx(bounds.Size);
        if (valuePx != _valuePx)
        {
            var font = MakeFont(valuePx);
            _valueFont.Dispose();
            _valueFont = font;
            _valuePx = valuePx;
            _dirty = true;
        }
        var pacePx = _placement.PaceTextPx(bounds.Size);
        if (pacePx != _pacePx)
        {
            var font = MakeFont(pacePx);
            _paceFont.Dispose();
            _paceFont = font;
            _pacePx = pacePx;
            _dirty = true;
        }

        if (_dirty) Render();
        // Mid-drag the mouse says where the window is, not the placement.
        if (Dragging) return;
        MoveTo(_placement.Resolve(bounds, Painted));
        Reveal();
    }

    private void FollowSlots(Rectangle bounds)
    {
        var rows = _preview ? SlotsPane.Samples : _rows;
        if (rows.Count == 0)
        {
            _slots.Conceal();
            return;
        }
        if (_slots.Fit(_slotsPlacement, bounds.Size)) _slotsDirty = true;
        if (bounds.Size != _slotsWindow)
        {
            _slotsWindow = bounds.Size;
            _slotsDirty = true;
        }
        if (_slotsDirty)
        {
            _slots.Render(_slotsPlacement, rows, bounds.Size, _preview);
            _slotsDirty = false;
        }
        if (_slots.Dragging) return;
        _slots.MoveTo(_slotsPlacement.Resolve(bounds, _slots.Painted));
        _slots.Reveal();
    }

    private void ConcealAll()
    {
        Conceal();
        _slots.Conceal();
    }

    /// <summary>
    /// The chat's own text contract, drawn our way: a bright core with a dark
    /// outline within reach, so it reads over any scenery the way the loot log
    /// does. Drawn into an alpha bitmap and handed to the compositor whole.
    /// </summary>
    private void Render()
    {
        _dirty = false;
        // Two lines, each in its own size, in the member's order. Each keeps
        // its place even when empty, so nothing jumps when a figure arrives.
        var value = (Text: _preview ? SampleValue : _line1, Font: _valueFont);
        var pace = (Text: _preview ? SamplePace : _line2, Font: _paceFont);
        var lines = _placement.PaceFirst ? new[] { pace, value } : new[] { value, pace };
        // The outline scales with the larger face; the padding leaves room for it.
        var outline = Math.Max(2f, Math.Max(_valueFont.Size, _paceFont.Size) / 9);
        var pad = outline + 2;

        var widths = new float[lines.Length];
        var heights = new float[lines.Length];
        float width = 0, height = 0;
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
        {
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (var i = 0; i < lines.Length; i++)
            {
                heights[i] = lines[i].Font.GetHeight(measure);
                height += heights[i];
                if (lines[i].Text.Length == 0) continue;
                widths[i] = measure.MeasureString(lines[i].Text, lines[i].Font, PointF.Empty, StringFormat.GenericTypographic).Width;
                width = Math.Max(width, widths[i]);
            }
        }

        var w = Math.Max(1, (int)Math.Ceiling(width + pad * 2));
        var h = Math.Max(1, (int)Math.Ceiling(height + pad * 2));
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            // In a preview the whole bitmap gets an alpha of 1 — invisible,
            // and enough for the compositor to count it as the window, so a
            // drag can start between the digits and not only on them.
            g.Clear(_preview ? Color.FromArgb(1, 0, 0, 0) : Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Winding, not the default even-odd: Inter's "4" is two contours
            // that overlap where the stem crosses the bar, and even-odd leaves
            // that overlap unfilled — a hole with the outline showing through
            // it (field, 0.7.0). Outer contours wind one way and counters the
            // other, so winding fills every glyph and still leaves the holes
            // in 0, 6, 8 and 9 open.
            using var path = new GraphicsPath { FillMode = FillMode.Winding };
            var y = pad;
            for (var i = 0; i < lines.Length; i++)
            {
                var (text, font) = lines[i];
                if (text.Length > 0)
                {
                    // Lines of two sizes line up the way the anchor does: on
                    // the edge they hang from, or on each other's centre.
                    var x = _placement.Column switch
                    {
                        0 => pad,
                        1 => pad + (width - widths[i]) / 2,
                        _ => pad + width - widths[i],
                    };
                    path.AddString(text, font.FontFamily, (int)font.Style, font.Size, new PointF(x, y), StringFormat.GenericTypographic);
                }
                y += heights[i];
            }
            using var pen = new Pen(Color.FromArgb(210, 0, 0, 0), outline * 2) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
            g.FillPath(Brushes.White, path);
        }
        Push(bitmap);
    }

    private static string Silver(long value) => value.ToString("N0", Inv);

    private static string Compact(long value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => (value / 1e9).ToString("0.##", Inv) + "B",
        >= 1_000_000 => (value / 1e6).ToString("0.#", Inv) + "M",
        >= 1_000 => (value / 1e3).ToString("0", Inv) + "K",
        _ => value.ToString(Inv),
    };

    /// <summary>
    /// Pearl.ttf, embedded beside the icon (PR #40): Inter SemiBold under the
    /// OFL, registered privately for this process — nothing is installed. The
    /// bytes have to outlive the collection, so they are pinned for the
    /// window's lifetime and freed with it. Both panes draw from it.
    /// </summary>
    private static (PrivateFontCollection? Fonts, IntPtr Memory) LoadTypeface()
    {
        try
        {
            using var stream = typeof(OverlayForm).Assembly.GetManifestResourceStream("LushbdoCompanion.Pearl.ttf");
            if (stream is null) return (null, IntPtr.Zero);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var fonts = new PrivateFontCollection();
            fonts.AddMemoryFont(memory, bytes.Length);
            if (fonts.Families.Length > 0) return (fonts, memory);
            fonts.Dispose();
            Marshal.FreeHGlobal(memory);
        }
        catch
        {
            // The figures still show, in the system face.
        }
        return (null, IntPtr.Zero);
    }

    private Font MakeFont(float px)
    {
        if (_fonts is { Families.Length: > 0 })
        {
            var family = _fonts.Families[0];
            foreach (var style in new[] { FontStyle.Regular, FontStyle.Bold })
                if (family.IsStyleAvailable(style)) return new Font(family, px, style, GraphicsUnit.Pixel);
        }
        return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _follow.Stop();
            _follow.Dispose();
            _icons.Loaded -= OnIconLoaded;
            _slots.Dispose();
            _valueFont.Dispose();
            _paceFont.Dispose();
            _fonts?.Dispose();
            if (_fontMemory != IntPtr.Zero) Marshal.FreeHGlobal(_fontMemory);
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
