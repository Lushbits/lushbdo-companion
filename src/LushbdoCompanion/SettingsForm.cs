namespace LushbdoCompanion;

public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly TextBox _token;
    private readonly TextBox _baseUrl;

    public SettingsForm(Settings settings)
    {
        _settings = settings;

        Text = "Lushbdo Companion — settings";
        Width = 520;
        Height = 230;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var tokenLabel = new Label { Text = "Device token (Settings → Devices on the site, shown once at pairing):", Left = 12, Top = 12, Width = 480 };
        _token = new TextBox { Left = 12, Top = 34, Width = 480, UseSystemPasswordChar = true };
        if (_settings.IsPaired) _token.PlaceholderText = "a token is saved — paste here only to replace it";

        var urlLabel = new Label { Text = "Site address:", Left = 12, Top = 70, Width = 480 };
        _baseUrl = new TextBox { Left = 12, Top = 92, Width = 480, Text = _settings.BaseUrl };

        var save = new Button { Text = "Save", Left = 336, Top = 134, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 417, Top = 134, Width = 75, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Apply();

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([tokenLabel, _token, urlLabel, _baseUrl, save, cancel]);
    }

    private void Apply()
    {
        var typed = _token.Text.Trim();
        if (typed.Length > 0) _settings.Token = typed;

        var url = _baseUrl.Text.Trim();
        if (url.Length > 0) _settings.BaseUrl = url;

        _settings.Save();
    }
}
