using System.Diagnostics;

namespace LushbdoCompanion;

public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly Label _status;
    private readonly TextBox _token;
    private readonly TextBox _baseUrl;

    public SettingsForm(Settings settings)
    {
        _settings = settings;

        Text = "LushBDO Companion — settings";
        // The layout below is laid out in 96-DPI pixels; Dpi auto-scaling is
        // what keeps it usable at 125 %/150 % now that the app is per-monitor
        // DPI aware for the capture side.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Width = 520;
        Height = 300;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _status = new Label { Left = 12, Top = 12, Width = 480, Font = new Font(Font, FontStyle.Bold) };

        var devices = new LinkLabel { Text = "Mint or revoke device tokens on the site (Settings → Devices)", Left = 12, Top = 36, Width = 480 };
        devices.LinkClicked += (_, _) => OpenSite("/settings/devices");

        var tokenLabel = new Label { Text = "Device token — shown once when you pair; each PC gets its own:", Left = 12, Top = 68, Width = 480 };
        _token = new TextBox { Left = 12, Top = 90, Width = 480, UseSystemPasswordChar = true };
        if (_settings.IsPaired) _token.PlaceholderText = "paste here only to replace the saved token";

        var urlLabel = new Label { Text = "Site address:", Left = 12, Top = 126, Width = 480 };
        _baseUrl = new TextBox { Left = 12, Top = 148, Width = 480, Text = _settings.BaseUrl };

        var version = new Label
        {
            Text = $"Version {UpdateChecker.Current.ToString(3)}",
            Left = 12, Top = 200, Width = 200, ForeColor = SystemColors.GrayText
        };

        var save = new Button { Text = "Save", Left = 336, Top = 194, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 417, Top = 194, Width = 75, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Apply();

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([_status, devices, tokenLabel, _token, urlLabel, _baseUrl, version, save, cancel]);

        RefreshStatus();
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

    private void Apply()
    {
        var typed = _token.Text.Trim();
        if (typed.Length > 0) _settings.Token = typed;

        var url = _baseUrl.Text.Trim();
        if (url.Length > 0) _settings.BaseUrl = url;

        _settings.Save();
        RefreshStatus();
    }
}
