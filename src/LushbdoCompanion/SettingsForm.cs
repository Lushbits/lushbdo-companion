using System.Diagnostics;

namespace LushbdoCompanion;

/// <summary>
/// The app's settings, in one window with pages down the left (#43): the
/// pairing, the rectangles and the silver-only mode, the overlay's switch and
/// placement, the item slots' placement (#52), and the diagnostics. It holds
/// what already existed as settings and nothing that would make it a
/// dashboard: the site is the product, and this is the smallest thing that
/// can hold a few switches and one live preview. The tray keeps only what is
/// done in the moment.
///
/// The Overlay and Item slots pages' preview is the overlay itself. While
/// either is open the real overlay windows draw samples over the game, so
/// every control changes what is on the game as it is changed and nothing is
/// mocked in here. That costs one repaint on the way in and one on the way
/// out, and nothing while the window is closed.
///
/// Modeless, on purpose: the log is the app's debugging surface and stays
/// readable with this open, and the tray keeps working. Every control here
/// applies as it is changed, the way the tray's switches always have; the
/// one exception is the pairing, where a half-pasted token must not save on
/// every keystroke, so that page keeps a Save button.
/// </summary>
public sealed class SettingsForm : Form
{
    /// <summary>
    /// What the window asks of the tray, which owns the watcher, the senders
    /// and the overlay window. The window edits and saves <see cref="Settings"/>
    /// itself where a change is only a value; these say that it did, so the
    /// running things follow — and carry the actions that are not values.
    /// </summary>
    public interface IHost
    {
        /// <summary>Pick a rectangle on a still of the game; on success it is saved and watching starts.</summary>
        Task PickRegionAsync(Settings.RegionKind kind);

        /// <summary>Drop a rectangle; the loot log's stops watching until one is picked again.</summary>
        Task ForgetRegionAsync(Settings.RegionKind kind);

        /// <summary>"Watch silver only" changed and is saved; a running watch is restarted the other way.</summary>
        Task SilverOnlyChanged();

        /// <summary>The Overlay page opened or closed: sample figures over the game meanwhile.</summary>
        void PreviewOverlay(bool on);

        /// <summary>The overlay's placement changed and is saved; draw it there.</summary>
        void OverlayPlaced();

        /// <summary>The item slots' placement changed and is saved; draw them there.</summary>
        void SlotsPlaced();

        /// <summary>"Show on the game window" changed and is saved.</summary>
        void OverlayToggled();

        /// <summary>Drop the cached item icons; the next paint fetches again.</summary>
        void ClearIcons();

        /// <summary>The token or the site address was saved.</summary>
        void PairingSaved();

        /// <summary>Post the fixed test batch to the site and say in the log what came back.</summary>
        Task SendTestBatchAsync();

        /// <summary>"Trace OCR to file" changed and is saved; a running watch switches at once.</summary>
        void TraceChanged();

        /// <summary>Ask GitHub for the newest release now and open its page if this one is behind.</summary>
        Task CheckForUpdatesAsync();
    }

    public enum Page { Pairing, Regions, Overlay, Items, Diagnostics }

    /// <summary>The two pages whose preview is the overlay itself: while either is open the samples are on the game.</summary>
    private static bool Previews(Page? page) => page is Page.Overlay or Page.Items;

    private static string Title(Page page) => page switch
    {
        Page.Items => "Item slots",
        _ => page.ToString(),
    };

    private static readonly Settings.RegionKind[] RegionKinds = [Settings.RegionKind.Loot, Settings.RegionKind.Marketplace];

    private readonly Settings _settings;
    private readonly IHost _host;
    private readonly Icon? _icon;
    private readonly ListBox _pages;
    private readonly Dictionary<Page, Panel> _panels = [];
    private Page? _current;
    private bool _loading; // the controls are being set from settings, not by the member

    // Pairing
    private readonly Label _status;
    private readonly TextBox _token;
    private readonly TextBox _baseUrl;

    // Regions
    private readonly Dictionary<Settings.RegionKind, Label> _regionStatus = [];
    private readonly Dictionary<Settings.RegionKind, Button> _forget = [];
    private readonly CheckBox _silverOnly;

    // Overlay
    private readonly CheckBox _show;
    private readonly RadioButton[] _anchors;
    private readonly NumericUpDown _offsetX;
    private readonly NumericUpDown _offsetY;
    private readonly TrackBar _valueSize;   // tenths of a percent
    private readonly Label _valueSizeValue;
    private readonly TrackBar _paceSize;
    private readonly Label _paceSizeValue;
    private readonly RadioButton _valueFirst;
    private readonly RadioButton _paceFirst;
    private readonly Label _previewNote;

    // Item slots
    private readonly RadioButton[] _slotAnchors;
    private readonly NumericUpDown _slotOffsetX;
    private readonly NumericUpDown _slotOffsetY;
    private readonly TrackBar _slotSize;       // tenths of a percent
    private readonly Label _slotSizeValue;
    private readonly TrackBar _slotSpacing;    // tenths of a percent
    private readonly Label _slotSpacingValue;
    private readonly RadioButton _slotsDown;
    private readonly RadioButton _slotsAcross;
    private readonly Label _slotsPreviewNote;

    // Diagnostics
    private readonly CheckBox _trace;
    private readonly Label _iconsNote;

    public SettingsForm(Settings settings, IHost host, Page open = Page.Pairing)
    {
        _settings = settings;
        _host = host;

        Text = "LushBDO Companion — settings";
        _icon = AppIcon.Window();
        if (_icon is not null) Icon = _icon;
        // The layout below is laid out in 96-DPI pixels; Dpi auto-scaling is
        // what keeps it usable at 125 %/150 % now that the app is per-monitor
        // DPI aware for the capture side.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(640, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _pages = new ListBox
        {
            Left = 12, Top = 12, Width = 120, Height = 396,
            IntegralHeight = false,
            ItemHeight = 24,
            DrawMode = DrawMode.OwnerDrawFixed,
        };
        _pages.DrawItem += DrawPage;
        foreach (var page in Enum.GetValues<Page>()) _pages.Items.Add(page);
        _pages.SelectedIndexChanged += (_, _) => { if (_pages.SelectedItem is Page page) ShowPage(page); };

        // The window is modeless, and a DialogResult closes only a modal one —
        // which is how 0.7.0–0.7.2 shipped a Close button that did nothing. The
        // button closes the window itself; CancelButton keeps Esc on it.
        var close = new Button { Text = "Close", Left = 553, Top = 422, Width = 75 };
        close.Click += (_, _) => Close();
        CancelButton = close;

        // --- Pairing ---------------------------------------------------------
        _status = new Label { Left = 0, Top = 0, Width = 480, Font = new Font(Font, FontStyle.Bold) };

        var devices = new LinkLabel { Text = "Mint or revoke device tokens on the site (Settings → Devices)", Left = 0, Top = 24, Width = 480 };
        devices.LinkClicked += (_, _) => OpenSite("/settings/devices");

        var tokenLabel = new Label { Text = "Device token — shown once when you pair; each PC gets its own:", Left = 0, Top = 56, Width = 480 };
        _token = new TextBox { Left = 0, Top = 78, Width = 480, UseSystemPasswordChar = true };
        if (_settings.IsPaired) _token.PlaceholderText = "paste here only to replace the saved token";

        var urlLabel = new Label { Text = "Site address:", Left = 0, Top = 114, Width = 480 };
        _baseUrl = new TextBox { Left = 0, Top = 136, Width = 480, Text = _settings.BaseUrl };

        var save = new Button { Text = "Save", Left = 405, Top = 172, Width = 75 };
        save.Click += (_, _) => SavePairing();

        var site = new LinkLabel { Text = "Open lushbdo.com", Left = 0, Top = 178, Width = 200 };
        site.LinkClicked += (_, _) => OpenSite("");

        // The proof of a pairing: a fixed batch, posted to a session that is
        // running on the site, with the site's answer in the log.
        var test = new Button { Text = "Send test batch", Left = 0, Top = 224, Width = 130 };
        test.Click += async (_, _) => { test.Enabled = false; try { await _host.SendTestBatchAsync(); } finally { test.Enabled = true; } };
        var testNote = Note(
            "Posts a fixed batch of pickups to your running gather session — start one on the site first — and " +
            "writes the site's reply to the log.",
            140, 224, 340, 52);

        var pairing = NewPage(Page.Pairing);
        pairing.Controls.AddRange([_status, devices, tokenLabel, _token, urlLabel, _baseUrl, save, site, test, testNote]);

        // --- Regions ---------------------------------------------------------
        // Each rectangle says exactly what it is set to, in pixels, because
        // that is the only way to tell two rectangles apart at a glance when
        // one of them is aimed wrong — which is the thing that actually goes
        // wrong (#22 field session).
        var regions = NewPage(Page.Regions);
        var top = 0;
        foreach (var kind in RegionKinds)
        {
            var loot = kind == Settings.RegionKind.Loot;
            var name = new Label { Text = loot ? "Loot log" : "Marketplace silver", Left = 0, Top = top, Width = 480, Font = new Font(Font, FontStyle.Bold) };
            var status = new Label { Left = 0, Top = top + 20, Width = 480 };
            _regionStatus[kind] = status;
            var pick = new Button { Text = "Pick…", Left = 0, Top = top + 42, Width = 75 };
            var forget = new Button { Text = "Forget", Left = 81, Top = top + 42, Width = 75 };
            _forget[kind] = forget;
            pick.Click += async (_, _) => await Run(pick, () => _host.PickRegionAsync(kind));
            forget.Click += async (_, _) => await Run(forget, () => _host.ForgetRegionAsync(kind));
            var note = Note(loot
                    ? "The chat tab filtered to item pickups. Drag around its text, starting just right of the " +
                      "System chip column; picking it starts watching."
                    : "Open the Central Market in-game first. Drag around its Warehouse Balance figure — the label, " +
                      "the number and nothing else — and keep buttons out of the rectangle: a hover overlay can " +
                      "cover the digits. Optional; without it your silver is simply not read.",
                0, top + 72, 480, loot ? 32 : 48);
            regions.Controls.AddRange([name, status, pick, forget, note]);
            top += loot ? 112 : 128;
        }

        _silverOnly = new CheckBox { Text = "Watch silver only (much lighter)", Left = 0, Top = top + 4, AutoSize = true };
        _silverOnly.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            _settings.SilverOnly = _silverOnly.Checked;
            _settings.Save();
            await Run(_silverOnly, _host.SilverOnlyChanged);
        };
        var silverNote = Note(
            "Skips the loot log entirely and reads only the silver rectangle. The loot log is what costs CPU — it " +
            "keys every captured frame and reads the chat — so this is far lighter on a laptop. Your loot " +
            "rectangle is kept, so switching back is one click.",
            0, top + 28, 480, 48);
        regions.Controls.AddRange([_silverOnly, silverNote]);

        // --- Overlay ---------------------------------------------------------
        _show = new CheckBox { Text = "Show on the game window while a session runs", Left = 0, Top = 0, AutoSize = true };
        _show.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.ShowOverlay = _show.Checked;
            _settings.Save();
            _host.OverlayToggled();
        };
        var showNote = Note(
            "The session's value and pace, as the site reports them, in a click-through window of our own — only " +
            "while a session is live and the game is in front. While this page is open it draws sample figures " +
            "instead: drag them on the game, or set the numbers below.",
            0, 24, 480, 52);

        var anchorLabel = new Label { Text = "Anchor", Left = 0, Top = 84, Width = 70 };
        _anchors = AnchorGrid(80, anchor => Apply(p => p with { Anchor = anchor }));
        var anchorNote = Note(
            "Where the figures hang from. Anchoring is what survives a resolution or window-size change; the " +
            "offset and size below are measured from here.",
            190, 84, 290, 60);

        var offsetLabel = new Label { Text = "Offset", Left = 0, Top = 184, Width = 70 };
        var xLabel = new Label { Text = "X", Left = 72, Top = 184, Width = 16 };
        _offsetX = Spinner(90, 180, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset, 0);
        var yLabel = new Label { Text = "Y", Left = 166, Top = 184, Width = 16 };
        _offsetY = Spinner(184, 180, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset, 0);
        _offsetX.ValueChanged += (_, _) => { if (!_loading) Apply(p => p with { OffsetX = (int)_offsetX.Value }); };
        _offsetY.ValueChanged += (_, _) => { if (!_loading) Apply(p => p with { OffsetY = (int)_offsetY.Value }); };
        var offsetNote = Note("Pixels in from the anchored edge; right and down from the centre.", 260, 184, 220, 40);

        // Sliders, not spinners (owner ask): a size is felt rather than known,
        // and the figures on the game follow the thumb as it moves. One per
        // line, in tenths of a percent, because the pace reads best a little
        // smaller than the value.
        var valueSizeLabel = new Label { Text = "Value size", Left = 0, Top = 224, Width = 70 };
        (_valueSize, _valueSizeValue) = Slider(216);
        _valueSize.ValueChanged += (_, _) =>
        {
            _valueSizeValue.Text = $"{_valueSize.Value / 10.0:0.0} % of height";
            if (!_loading) Apply(p => p with { TextPct = _valueSize.Value / 10.0 });
        };
        var paceSizeLabel = new Label { Text = "Pace size", Left = 0, Top = 258, Width = 70 };
        (_paceSize, _paceSizeValue) = Slider(250);
        _paceSize.ValueChanged += (_, _) =>
        {
            _paceSizeValue.Text = $"{_paceSize.Value / 10.0:0.0} % of height";
            if (!_loading) Apply(p => p with { PaceTextPct = _paceSize.Value / 10.0 });
        };

        var orderLabel = new Label { Text = "Order", Left = 0, Top = 292, Width = 70 };
        _valueFirst = new RadioButton { Text = "Value, then pace", Left = 72, Top = 290, AutoSize = true };
        _paceFirst = new RadioButton { Text = "Pace, then value", Left = 210, Top = 290, AutoSize = true };
        _paceFirst.CheckedChanged += (_, _) =>
        {
            if (!_loading) Apply(p => p with { PaceFirst = _paceFirst.Checked });
        };

        var reset = new Button { Text = "Back to the defaults", Left = 72, Top = 322, Width = 170 };
        reset.Click += (_, _) =>
        {
            LoadPlacement(OverlayPlacement.Default);
            Apply(_ => OverlayPlacement.Default);
        };

        _previewNote = Note("", 0, 356, 480, 40);

        var overlay = NewPage(Page.Overlay);
        overlay.Controls.AddRange([_show, showNote, anchorLabel, anchorNote, offsetLabel, xLabel, _offsetX, yLabel, _offsetY,
            offsetNote, valueSizeLabel, _valueSize, _valueSizeValue, paceSizeLabel, _paceSize, _paceSizeValue,
            orderLabel, _valueFirst, _paceFirst, reset, _previewNote]);
        overlay.Controls.AddRange(_anchors);

        // --- Item slots (#52) --------------------------------------------------
        // The same controls as the figures — anchor, offset, size — for the
        // group of three, plus the two the figures do not need: how far apart
        // the slots sit and which way they run. One anchor for the group
        // rather than one per slot (owner ruling, 2026-09-10). Which items
        // fill the slots is not here at all: that is set on the site.
        var slotsNote = Note(
            "Up to three items you put in slots on the site's session page, each as its icon and the count so far. " +
            "Which items is set on the site — this page only says where they go. While it is open, three sample " +
            "slots are drawn on the game: drag them, or set the numbers below.",
            0, 0, 480, 52);

        var slotAnchorLabel = new Label { Text = "Anchor", Left = 0, Top = 64, Width = 70 };
        _slotAnchors = AnchorGrid(60, anchor => ApplySlots(p => p with { Anchor = anchor }));
        var slotAnchorNote = Note(
            "Where the group of three hangs from. The offset, size and spacing below are measured from here.",
            190, 64, 290, 48);

        var slotOffsetLabel = new Label { Text = "Offset", Left = 0, Top = 164, Width = 70 };
        var slotXLabel = new Label { Text = "X", Left = 72, Top = 164, Width = 16 };
        _slotOffsetX = Spinner(90, 160, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset, 0);
        var slotYLabel = new Label { Text = "Y", Left = 166, Top = 164, Width = 16 };
        _slotOffsetY = Spinner(184, 160, -OverlayPlacement.MaxOffset, OverlayPlacement.MaxOffset, 0);
        _slotOffsetX.ValueChanged += (_, _) => { if (!_loading) ApplySlots(p => p with { OffsetX = (int)_slotOffsetX.Value }); };
        _slotOffsetY.ValueChanged += (_, _) => { if (!_loading) ApplySlots(p => p with { OffsetY = (int)_slotOffsetY.Value }); };
        var slotOffsetNote = Note("Pixels in from the anchored edge; right and down from the centre.", 260, 164, 220, 40);

        var slotSizeLabel = new Label { Text = "Size", Left = 0, Top = 204, Width = 70 };
        (_slotSize, _slotSizeValue) = Slider(196, OverlayPlacement.MinTextPct, OverlayPlacement.MaxTextPct);
        _slotSize.ValueChanged += (_, _) =>
        {
            _slotSizeValue.Text = $"{_slotSize.Value / 10.0:0.0} % of height";
            if (!_loading) ApplySlots(p => p with { TextPct = _slotSize.Value / 10.0 });
        };
        var slotSpacingLabel = new Label { Text = "Spacing", Left = 0, Top = 238, Width = 70 };
        (_slotSpacing, _slotSpacingValue) = Slider(230, SlotsPlacement.MinSpacingPct, SlotsPlacement.MaxSpacingPct);
        _slotSpacing.ValueChanged += (_, _) =>
        {
            _slotSpacingValue.Text = $"{_slotSpacing.Value / 10.0:0.0} % of height";
            if (!_loading) ApplySlots(p => p with { SpacingPct = _slotSpacing.Value / 10.0 });
        };

        var flowLabel = new Label { Text = "Direction", Left = 0, Top = 272, Width = 70 };
        _slotsDown = new RadioButton { Text = "Stacked, down", Left = 72, Top = 270, AutoSize = true };
        _slotsAcross = new RadioButton { Text = "Side by side, across", Left = 210, Top = 270, AutoSize = true };
        _slotsAcross.CheckedChanged += (_, _) =>
        {
            if (!_loading) ApplySlots(p => p with { Flow = _slotsAcross.Checked ? SlotFlow.Horizontal : SlotFlow.Vertical });
        };

        var slotsReset = new Button { Text = "Back to the defaults", Left = 72, Top = 302, Width = 170 };
        slotsReset.Click += (_, _) =>
        {
            LoadSlots(SlotsPlacement.Default);
            ApplySlots(_ => SlotsPlacement.Default);
        };

        _slotsPreviewNote = Note("", 0, 340, 480, 52);

        var items = NewPage(Page.Items);
        items.Controls.AddRange([slotsNote, slotAnchorLabel, slotAnchorNote, slotOffsetLabel, slotXLabel, _slotOffsetX,
            slotYLabel, _slotOffsetY, slotOffsetNote, slotSizeLabel, _slotSize, _slotSizeValue, slotSpacingLabel,
            _slotSpacing, _slotSpacingValue, flowLabel, _slotsDown, _slotsAcross, slotsReset, _slotsPreviewNote]);
        items.Controls.AddRange(_slotAnchors);

        // --- Diagnostics -----------------------------------------------------
        _trace = new CheckBox { Text = "Trace OCR to file", Left = 0, Top = 0, AutoSize = true };
        _trace.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.TraceOcr = _trace.Checked;
            _settings.Save();
            _host.TraceChanged();
        };
        var traceNote = Note(
            "Every OCR row, vote and gate decision, with snapshots of the frames, written next to settings.json in " +
            "%APPDATA%\\lushbdo-companion. It is what a misread can be diagnosed from, and it grows fast — leave " +
            "it off unless a bug needs it.",
            0, 24, 480, 52);

        var version = new Label { Text = $"Version {UpdateChecker.Current.ToString(3)}", Left = 0, Top = 96, Width = 300 };
        var update = new Button { Text = "Check for updates", Left = 0, Top = 120, Width = 130 };
        update.Click += async (_, _) => await Run(update, _host.CheckForUpdatesAsync);
        var updateNote = Note(
            "Compares this version to the newest GitHub release and opens its page if you are behind. The app " +
            "checks by itself at startup and once a day; it never updates itself.",
            140, 120, 340, 48);

        // The icons the slots draw are extracted artwork on this disk, kept
        // so they are fetched once. Said here, with the way to drop them.
        var clearIcons = new Button { Text = "Clear cached icons", Left = 0, Top = 180, Width = 130 };
        _iconsNote = Note("", 140, 180, 340, 64);
        clearIcons.Click += (_, _) =>
        {
            _host.ClearIcons();
            RefreshIcons();
        };

        var diagnostics = NewPage(Page.Diagnostics);
        diagnostics.Controls.AddRange([_trace, traceNote, version, update, updateNote, clearIcons, _iconsNote]);

        Controls.Add(_pages);
        Controls.Add(close);
        foreach (var panel in _panels.Values) Controls.Add(panel);

        RefreshStatus();
        RefreshRegions();
        RefreshIcons();
        LoadPlacement(_settings.Overlay);
        LoadSlots(_settings.Slots);
        _loading = true;
        _show.Checked = _settings.ShowOverlay;
        _trace.Checked = _settings.TraceOcr;
        _loading = false;
        _pages.SelectedItem = open;
    }

    /// <summary>Bring the window to a page — what the tray does when the window is already up.</summary>
    public void Open(Page page) => _pages.SelectedItem = page;

    /// <summary>The figures were dragged on the game: show where they ended up, without putting it back.</summary>
    public void ShowPlacement(OverlayPlacement placement) => LoadPlacement(placement);

    /// <summary>The slots were dragged on the game, likewise.</summary>
    public void ShowSlots(SlotsPlacement placement) => LoadSlots(placement);

    private Panel NewPage(Page page)
    {
        var panel = new Panel { Left = 148, Top = 12, Width = 480, Height = 396, Visible = false };
        _panels[page] = panel;
        return panel;
    }

    /// <summary>
    /// Nine cells, drawn as toggle buttons: the pressed one is the anchor.
    /// Radio buttons in one container exclude each other by themselves. The
    /// figures and the slots each have one, and each says what a press means.
    /// </summary>
    private RadioButton[] AnchorGrid(int top, Action<OverlayAnchor> chosen)
    {
        var cells = new RadioButton[9];
        var glyphs = new[] { "↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘" };
        for (var i = 0; i < 9; i++)
        {
            var anchor = (OverlayAnchor)i;
            var cell = new RadioButton
            {
                Appearance = Appearance.Button,
                Text = glyphs[i],
                TextAlign = ContentAlignment.MiddleCenter,
                Left = 72 + i % 3 * 36, Top = top + i / 3 * 30, Width = 34, Height = 28,
                Tag = anchor,
            };
            cell.CheckedChanged += (_, _) =>
            {
                if (_loading || !cell.Checked) return;
                chosen(anchor);
            };
            cells[i] = cell;
        }
        return cells;
    }

    private static Label Note(string text, int left, int top, int width, int height) => new()
    {
        Text = text, Left = left, Top = top, Width = width, Height = height, ForeColor = SystemColors.GrayText,
    };

    private static NumericUpDown Spinner(int left, int top, decimal min, decimal max, int decimals) => new()
    {
        Left = left, Top = top, Width = 64, Minimum = min, Maximum = max, DecimalPlaces = decimals,
        TextAlign = HorizontalAlignment.Right,
    };

    /// <summary>A text-size slider over the placement's range, in tenths of a percent, and the label that reads it out.</summary>
    private static (TrackBar Bar, Label Value) Slider(int top) =>
        Slider(top, OverlayPlacement.MinTextPct, OverlayPlacement.MaxTextPct);

    /// <summary>A slider over any range of percents, in tenths, and the label that reads it out.</summary>
    private static (TrackBar Bar, Label Value) Slider(int top, double minPct, double maxPct) => (
        new TrackBar
        {
            Left = 72, Top = top, Width = 290, Height = 32, AutoSize = false,
            Minimum = (int)(minPct * 10), Maximum = (int)(maxPct * 10),
            TickFrequency = 5, SmallChange = 1, LargeChange = 5,
        },
        new Label { Left = 366, Top = top + 8, Width = 114 });

    private static int Tenths(TrackBar bar, double pct) => Math.Clamp((int)Math.Round(pct * 10), bar.Minimum, bar.Maximum);

    /// <summary>
    /// One of the tray's actions, from a button: the button is down while it
    /// runs (a pick opens a full-screen still and a second click would open
    /// another), and the Regions page re-reads the settings when it is done,
    /// since a pick or a forget is what changes them.
    /// </summary>
    private async Task Run(Control control, Func<Task> action)
    {
        control.Enabled = false;
        try
        {
            await action();
        }
        finally
        {
            if (!IsDisposed)
            {
                control.Enabled = true;
                RefreshRegions();
            }
        }
    }

    /// <summary>The page list, drawn by hand so the rows are tall enough to click at and read like tabs.</summary>
    private void DrawPage(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();
        var text = _pages.Items[e.Index] is Page page ? Title(page) : "";
        var selected = (e.State & DrawItemState.Selected) != 0;
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, e.Bounds with { X = e.Bounds.X + 8 },
            selected ? SystemColors.HighlightText : SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private void ShowPage(Page page)
    {
        if (_current == page) return;
        // The preview is one thing shared by two pages: it stays up between
        // them, and comes down only on the way to a page that has none.
        var wasPreviewing = Previews(_current);
        if (wasPreviewing && !Previews(page)) _host.PreviewOverlay(false);
        foreach (var (kind, panel) in _panels) panel.Visible = kind == page;
        _current = page;
        if (page == Page.Regions) RefreshRegions();
        if (page == Page.Diagnostics) RefreshIcons();
        if (!Previews(page)) return;

        // The one thing the page cannot show on its own: whether there is a
        // game window to draw over. Checked once on the way in; the overlay
        // keeps looking by itself and appears the moment the game is up.
        var gameUp = GameWindow.Find() is not null;
        _previewNote.Text = gameUp
            ? $"Sample figures — {OverlayForm.SampleValue} and {OverlayForm.SamplePace} — are on the game now. " +
              "Drag them to where you want them; the anchor and offset follow. Nothing here needs a session."
            : $"Black Desert's window was not found, so there is nothing to draw over yet. Start the game and the " +
              $"sample figures ({OverlayForm.SampleValue} and {OverlayForm.SamplePace}) appear by themselves.";
        _slotsPreviewNote.Text = gameUp
            ? "Three sample slots are on the game now. Drag them to where you want them; the anchor and offset " +
              "follow. Nothing here needs a session or an item."
            : "Black Desert's window was not found, so there is nothing to draw over yet. Start the game and " +
              "three sample slots appear by themselves.";
        if (!wasPreviewing) _host.PreviewOverlay(true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (Previews(_current)) _host.PreviewOverlay(false);
        base.OnFormClosed(e);
    }

    /// <summary>What the icon cache holds on disk, for the Diagnostics page to say.</summary>
    private void RefreshIcons()
    {
        var (files, bytes) = IconCache.OnDisk();
        _iconsNote.Text = (files == 0
                ? "No item icons are cached yet. "
                : $"{files} item icon{(files == 1 ? "" : "s")} ({bytes / 1024.0:0} KB) cached. ") +
            "The overlay's item slots fetch each icon from the site once and keep it under " +
            "%LOCALAPPDATA%\\lushbdo-companion\\icons. Clearing costs one fetch per slot next time.";
    }

    /// <summary>What each rectangle is set to, in the game window's pixels, or that it is not.</summary>
    private void RefreshRegions()
    {
        foreach (var kind in RegionKinds)
        {
            var rect = _settings.RegionFor(kind);
            _regionStatus[kind].Text = rect is { } r
                ? $"{r.Width}×{r.Height} at ({r.X}, {r.Y}) in the game window"
                : "not picked yet";
            _regionStatus[kind].ForeColor = rect is null ? Color.FromArgb(196, 132, 42) : SystemColors.ControlText;
            _forget[kind].Enabled = rect is not null;
        }
        _loading = true;
        _silverOnly.Checked = _settings.SilverOnly;
        _loading = false;
    }

    /// <summary>Put a placement on the controls without the controls putting it back.</summary>
    private void LoadPlacement(OverlayPlacement placement)
    {
        _loading = true;
        try
        {
            foreach (var cell in _anchors) cell.Checked = (OverlayAnchor)cell.Tag! == placement.Anchor;
            _offsetX.Value = placement.OffsetX;
            _offsetY.Value = placement.OffsetY;
            _valueSize.Value = Tenths(_valueSize, placement.TextPct);
            _valueSizeValue.Text = $"{_valueSize.Value / 10.0:0.0} % of height";
            _paceSize.Value = Tenths(_paceSize, placement.PaceTextPct);
            _paceSizeValue.Text = $"{_paceSize.Value / 10.0:0.0} % of height";
            _paceFirst.Checked = placement.PaceFirst;
            _valueFirst.Checked = !placement.PaceFirst;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>One control changed: save the placement and draw it. Each change is what the member sees.</summary>
    private void Apply(Func<OverlayPlacement, OverlayPlacement> change)
    {
        _settings.Overlay = change(_settings.Overlay).Clamped();
        _settings.Save();
        _host.OverlayPlaced();
    }

    /// <summary>Put the slots' placement on the controls without the controls putting it back.</summary>
    private void LoadSlots(SlotsPlacement placement)
    {
        _loading = true;
        try
        {
            foreach (var cell in _slotAnchors) cell.Checked = (OverlayAnchor)cell.Tag! == placement.Anchor;
            _slotOffsetX.Value = placement.OffsetX;
            _slotOffsetY.Value = placement.OffsetY;
            _slotSize.Value = Tenths(_slotSize, placement.TextPct);
            _slotSizeValue.Text = $"{_slotSize.Value / 10.0:0.0} % of height";
            _slotSpacing.Value = Tenths(_slotSpacing, placement.SpacingPct);
            _slotSpacingValue.Text = $"{_slotSpacing.Value / 10.0:0.0} % of height";
            _slotsAcross.Checked = placement.Flow == SlotFlow.Horizontal;
            _slotsDown.Checked = placement.Flow != SlotFlow.Horizontal;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>One slot control changed: save and draw, the figures' rule.</summary>
    private void ApplySlots(Func<SlotsPlacement, SlotsPlacement> change)
    {
        _settings.Slots = change(_settings.Slots).Clamped();
        _settings.Save();
        _host.SlotsPlaced();
    }

    private void RefreshStatus()
    {
        if (_settings.IsPaired)
        {
            var token = _settings.Token;
            var hint = token.Length >= 4 ? token[^4..] : "";
            _status.Text = $"Paired — token ending …{hint}";
            _status.ForeColor = Color.FromArgb(46, 160, 122);
        }
        else
        {
            _status.Text = "Not paired — paste a device token below.";
            _status.ForeColor = Color.FromArgb(196, 132, 42);
        }
    }

    private void OpenSite(string path)
    {
        var baseUrl = _baseUrl.Text.Trim();
        if (baseUrl.Length == 0) baseUrl = _settings.BaseUrl;
        Process.Start(new ProcessStartInfo(baseUrl.TrimEnd('/') + path) { UseShellExecute = true });
    }

    private void SavePairing()
    {
        var typed = _token.Text.Trim();
        if (typed.Length > 0) _settings.Token = typed;

        var url = _baseUrl.Text.Trim();
        if (url.Length > 0) _settings.BaseUrl = url;

        _settings.Save();
        _token.Clear();
        if (_settings.IsPaired) _token.PlaceholderText = "paste here only to replace the saved token";
        RefreshStatus();
        _host.PairingSaved();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}
