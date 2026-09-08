using System.Diagnostics;

namespace LushbdoCompanion;

/// <summary>
/// The app's settings, in one window with pages down the left (#43). It holds
/// what already existed as settings — the pairing, and the overlay's switch
/// and placement — and nothing that would make it a dashboard: the site is
/// the product, and this is the smallest thing that can hold a few switches
/// and one live preview.
///
/// The Overlay page's preview is the overlay itself. While the page is open
/// the real overlay window draws sample figures over the game, so every
/// control changes what is on the game as it is changed and nothing is
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
    /// What the window asks of the tray, which owns the overlay window and
    /// the watcher. The window edits and saves <see cref="Settings"/> itself;
    /// these say that it did, so the running things follow.
    /// </summary>
    public interface IHost
    {
        /// <summary>The Overlay page opened or closed: sample figures over the game meanwhile.</summary>
        void PreviewOverlay(bool on);

        /// <summary>The overlay's placement changed and is saved; draw it there.</summary>
        void OverlayPlaced();

        /// <summary>"Show on the game window" changed and is saved.</summary>
        void OverlayToggled();

        /// <summary>The token or the site address was saved.</summary>
        void PairingSaved();
    }

    public enum Page { Pairing, Overlay }

    private readonly Settings _settings;
    private readonly IHost _host;
    private readonly Icon? _icon;
    private readonly ListBox _pages;
    private readonly Dictionary<Page, Panel> _panels = [];
    private Page? _current;

    // Pairing
    private readonly Label _status;
    private readonly TextBox _token;
    private readonly TextBox _baseUrl;

    // Overlay
    private readonly CheckBox _show;
    private readonly RadioButton[] _anchors;
    private readonly NumericUpDown _offsetX;
    private readonly NumericUpDown _offsetY;
    private readonly NumericUpDown _size;
    private readonly Label _previewNote;
    private bool _loading; // the controls are being set from settings, not by the member

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
        ClientSize = new Size(640, 404);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _pages = new ListBox
        {
            Left = 12, Top = 12, Width = 120, Height = 340,
            IntegralHeight = false,
            ItemHeight = 24,
            DrawMode = DrawMode.OwnerDrawFixed,
        };
        _pages.DrawItem += DrawPage;
        foreach (var page in Enum.GetValues<Page>()) _pages.Items.Add(page);
        _pages.SelectedIndexChanged += (_, _) => { if (_pages.SelectedItem is Page page) ShowPage(page); };

        var close = new Button { Text = "Close", Left = 553, Top = 366, Width = 75, DialogResult = DialogResult.Cancel };
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

        var version = new Label
        {
            Text = $"Version {UpdateChecker.Current.ToString(3)}",
            Left = 0, Top = 178, Width = 200, ForeColor = SystemColors.GrayText
        };

        var pairing = NewPage(Page.Pairing);
        pairing.Controls.AddRange([_status, devices, tokenLabel, _token, urlLabel, _baseUrl, save, version]);

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
            "instead, so each change below shows on the game as you make it.",
            0, 24, 480, 52);

        // Nine cells, drawn as toggle buttons: the pressed one is the anchor.
        // Radio buttons in one container exclude each other by themselves.
        var anchorLabel = new Label { Text = "Anchor", Left = 0, Top = 84, Width = 70 };
        _anchors = new RadioButton[9];
        var glyphs = new[] { "↖", "↑", "↗", "←", "•", "→", "↙", "↓", "↘" };
        for (var i = 0; i < 9; i++)
        {
            var anchor = (OverlayAnchor)i;
            var cell = new RadioButton
            {
                Appearance = Appearance.Button,
                Text = glyphs[i],
                TextAlign = ContentAlignment.MiddleCenter,
                Left = 72 + i % 3 * 36, Top = 80 + i / 3 * 30, Width = 34, Height = 28,
                Tag = anchor,
            };
            cell.CheckedChanged += (_, _) =>
            {
                if (_loading || !cell.Checked) return;
                Apply(p => p with { Anchor = anchor });
            };
            _anchors[i] = cell;
        }
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

        var sizeLabel = new Label { Text = "Size", Left = 0, Top = 224, Width = 70 };
        _size = Spinner(72, 220, (decimal)OverlayPlacement.MinTextPct, (decimal)OverlayPlacement.MaxTextPct, 1);
        _size.Increment = 0.1m;
        _size.ValueChanged += (_, _) => { if (!_loading) Apply(p => p with { TextPct = (double)_size.Value }); };
        var sizeNote = new Label { Text = "% of the game window's height", Left = 144, Top = 224, Width = 300 };

        var reset = new Button { Text = "Back to the default spot", Left = 72, Top = 260, Width = 170 };
        reset.Click += (_, _) =>
        {
            LoadPlacement(OverlayPlacement.Default);
            Apply(_ => OverlayPlacement.Default);
        };

        _previewNote = Note("", 0, 300, 480, 40);

        var overlay = NewPage(Page.Overlay);
        overlay.Controls.AddRange([_show, showNote, anchorLabel, anchorNote, offsetLabel, xLabel, _offsetX, yLabel, _offsetY,
            offsetNote, sizeLabel, _size, sizeNote, reset, _previewNote]);
        overlay.Controls.AddRange(_anchors);

        Controls.Add(_pages);
        Controls.Add(close);
        foreach (var panel in _panels.Values) Controls.Add(panel);

        RefreshStatus();
        LoadPlacement(_settings.Overlay);
        _loading = true;
        _show.Checked = _settings.ShowOverlay;
        _loading = false;
        _pages.SelectedItem = open;
    }

    /// <summary>Bring the window to a page — what the tray does when the window is already up.</summary>
    public void Open(Page page) => _pages.SelectedItem = page;

    private Panel NewPage(Page page)
    {
        var panel = new Panel { Left = 148, Top = 12, Width = 480, Height = 340, Visible = false };
        _panels[page] = panel;
        return panel;
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

    /// <summary>The page list, drawn by hand so the rows are tall enough to click at and read like tabs.</summary>
    private void DrawPage(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();
        var text = _pages.Items[e.Index]?.ToString() ?? "";
        var selected = (e.State & DrawItemState.Selected) != 0;
        TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, e.Bounds with { X = e.Bounds.X + 8 },
            selected ? SystemColors.HighlightText : SystemColors.ControlText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    private void ShowPage(Page page)
    {
        if (_current == page) return;
        if (_current == Page.Overlay) _host.PreviewOverlay(false);
        foreach (var (kind, panel) in _panels) panel.Visible = kind == page;
        _current = page;
        if (page != Page.Overlay) return;

        // The one thing the page cannot show on its own: whether there is a
        // game window to draw over. Checked once on the way in; the overlay
        // keeps looking by itself and appears the moment the game is up.
        _previewNote.Text = GameWindow.Find() is null
            ? $"Black Desert's window was not found, so there is nothing to draw over yet. Start the game and the " +
              $"sample figures ({OverlayForm.SampleValue} and {OverlayForm.SamplePace}) appear by themselves."
            : $"Sample figures — {OverlayForm.SampleValue} and {OverlayForm.SamplePace} — are on the game now. " +
              "Nothing here needs a session.";
        _host.PreviewOverlay(true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_current == Page.Overlay) _host.PreviewOverlay(false);
        base.OnFormClosed(e);
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
            _size.Value = (decimal)placement.TextPct;
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
