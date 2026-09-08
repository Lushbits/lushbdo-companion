using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LushbdoCompanion;

/// <summary>
/// Two figures over the game window while a session runs (#39): what the
/// session is worth so far, and its pace in silver per hour. Nothing else on
/// it — no log, no list, no controls. One glance that saves an alt-tab.
///
/// How it stays in the same class as everything else here: this is a
/// separate top-most, click-through, never-activated layered window in our
/// own process, placed over the game window's rectangle — the same thing as
/// dragging a browser over the game. Nothing is injected, nothing is hooked,
/// nothing touches the game's process; the game is found the way the picker
/// finds it and followed with plain window queries. Capture is per-window,
/// so this window never appears in the frames OCR reads.
///
/// The figures are levels, like the silver balance: they are whatever the
/// site last reported on an ingest reply, and the app posts exactly when the
/// value changes, so nothing polls. Until the site carries a value
/// (bdo#724) the reply has an item count and gathering time, and that is
/// what shows. Hidden whenever there is nothing honest to show — no running
/// session, a pause, the game not in front, the game gone — and repainted
/// only when a figure changes. Following the window is a position query
/// four times a second and nothing more.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;

    private const float DefaultTextPx = 22;
    private const float MinTextPx = 12;
    private const float MaxTextPx = 56;
    private const int DefaultTopMargin = 6;

    private static readonly TimeSpan FollowPace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FindPace = TimeSpan.FromSeconds(5);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly System.Windows.Forms.Timer _follow;
    private readonly PrivateFontCollection? _fonts;
    private readonly IntPtr _fontMemory;
    private Font _font;

    private Rectangle? _anchor;          // window-relative: where the member put it, or null for the default spot
    private string _line1 = "";
    private string _line2 = "";
    private bool _live;                  // the site last reported a running session
    private bool _dirty = true;          // a figure changed since the last paint
    private bool _shown;
    private Size _painted;
    private Point _shownAt = new(int.MinValue, int.MinValue);
    private IntPtr _game;
    private DateTime _nextFind = DateTime.MinValue;

    public OverlayForm(Rectangle? anchor)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "LushBDO Companion overlay";

        (_fonts, _fontMemory) = LoadTypeface();
        _font = MakeFont(DefaultTextPx);
        Place(anchor);

        _ = Handle; // the layered window exists before the first figure arrives
        _follow = new System.Windows.Forms.Timer { Interval = (int)FollowPace.TotalMilliseconds };
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost;
            return p;
        }
    }

    /// <summary>
    /// Where to draw, in game-window pixels. The rectangle's top-left is the
    /// spot and its height sets the text size — two lines fill it. Null is
    /// the default: top centre of the game window, where nothing of the
    /// game's own UI sits at rest.
    /// </summary>
    public void Place(Rectangle? anchor)
    {
        _anchor = anchor;
        var px = anchor is { } a ? Math.Clamp(a.Height * 0.38f, MinTextPx, MaxTextPx) : DefaultTextPx;
        var font = MakeFont(px);
        _font.Dispose();
        _font = font;
        _dirty = true;
        Follow();
    }

    /// <summary>
    /// What the site said about the session on its latest reply; null when
    /// it said the run is not live. Levels, not events: the same figures
    /// twice are nothing to repaint.
    /// </summary>
    public void Report(IngestClient.SessionInfo? session)
    {
        var live = session is not null;
        var line1 = session switch
        {
            null => "",
            { Value: { } value } => Silver(value),
            { } s => $"{s.Items} item{(s.Items == 1 ? "" : "s")}",
        };
        var line2 = session switch
        {
            null => "",
            { PerHour: { } pace } => Compact(pace) + "/h",
            { } s => Elapsed(s.ElapsedSec),
        };
        if (live == _live && line1 == _line1 && line2 == _line2) return;
        _live = live;
        _line1 = line1;
        _line2 = line2;
        _dirty = true;
        Follow();
    }

    private void Follow()
    {
        if (IsDisposed || !_live)
        {
            Conceal();
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
                Conceal();
                return;
            }
            _nextFind = now + FindPace;
            _game = GameWindow.Find()?.Hwnd ?? IntPtr.Zero;
            if (_game == IntPtr.Zero)
            {
                Conceal();
                return;
            }
        }

        // Only over the game while the game is in front: a member who tabbed
        // out to the site does not want session figures on top of it.
        if (GetForegroundWindow() != _game)
        {
            Conceal();
            return;
        }
        var bounds = GameWindow.BoundsOf(_game);
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            Conceal();
            return;
        }

        if (_dirty) Render();
        var at = _anchor is { } a
            ? new Point(bounds.X + a.X, bounds.Y + a.Y)
            : new Point(bounds.X + (bounds.Width - _painted.Width) / 2, bounds.Y + DefaultTopMargin);
        if (at != _shownAt)
        {
            SetWindowPos(Handle, HwndTopmost, at.X, at.Y, 0, 0, SwpNoSize | SwpNoActivate);
            _shownAt = at;
        }
        if (!_shown)
        {
            ShowWindow(Handle, SwShowNoActivate);
            _shown = true;
        }
    }

    private void Conceal()
    {
        if (!_shown || IsDisposed) return;
        ShowWindow(Handle, SwHide);
        _shown = false;
    }

    /// <summary>
    /// The chat's own text contract, drawn our way: a bright core with a dark
    /// outline within reach, so it reads over any scenery the way the loot log
    /// does. Drawn into an alpha bitmap and handed to the compositor whole;
    /// there is no WM_PAINT and nothing for the game to redraw over.
    /// </summary>
    private void Render()
    {
        _dirty = false;
        var lines = new[] { _line1, _line2 };
        var outline = Math.Max(2f, _font.Size / 9);
        var pad = outline + 2;

        float width;
        float lineHeight;
        using (var measure = Graphics.FromHwnd(IntPtr.Zero))
        {
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            lineHeight = _font.GetHeight(measure);
            width = 0;
            foreach (var line in lines)
                if (line.Length > 0) width = Math.Max(width, measure.MeasureString(line, _font, PointF.Empty, StringFormat.GenericTypographic).Width);
        }

        var w = Math.Max(1, (int)Math.Ceiling(width + pad * 2));
        var h = Math.Max(1, (int)Math.Ceiling(lineHeight * lines.Length + pad * 2));
        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var path = new GraphicsPath();
            var y = pad;
            foreach (var line in lines)
            {
                if (line.Length > 0)
                    path.AddString(line, _font.FontFamily, (int)_font.Style, _font.Size, new PointF(pad, y), StringFormat.GenericTypographic);
                y += lineHeight;
            }
            using var pen = new Pen(Color.FromArgb(210, 0, 0, 0), outline * 2) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
            g.FillPath(Brushes.White, path);
        }
        _painted = bitmap.Size;
        Push(bitmap);
    }

    private void Push(Bitmap bitmap)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memDc, hBitmap);
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = AcSrcOver, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memDc, previous);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static string Silver(long value) => value.ToString("N0", Inv);

    private static string Compact(long value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => (value / 1e9).ToString("0.##", Inv) + "B",
        >= 1_000_000 => (value / 1e6).ToString("0.#", Inv) + "M",
        >= 1_000 => (value / 1e3).ToString("0", Inv) + "K",
        _ => value.ToString(Inv),
    };

    private static string Elapsed(long seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", Inv);

    /// <summary>
    /// Pearl.ttf, embedded beside the icon (PR #40): Inter SemiBold under the
    /// OFL, registered privately for this process — nothing is installed. The
    /// bytes have to outlive the collection, so they are pinned for the
    /// window's lifetime and freed with it.
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
            _font.Dispose();
            _fonts?.Dispose();
            if (_fontMemory != IntPtr.Zero) Marshal.FreeHGlobal(_fontMemory);
        }
        base.Dispose(disposing);
    }

    // --- Win32 -----------------------------------------------------------

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int UlwAlpha = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dst, ref SIZE size, IntPtr srcDc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
