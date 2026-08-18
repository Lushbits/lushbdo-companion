using System.Diagnostics;

namespace LushbdoCompanion;

/// <summary>
/// The whole visible app: a tray icon, its menu, and the windows it opens.
/// There is deliberately no main window — the site is the product, this is the
/// typing you no longer do.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly Settings _settings;
    private readonly IngestClient _client;
    private readonly LogWindow _log = new();
    private readonly System.Windows.Forms.Timer _updateTimer;
    private bool _updateBalloonShown;

    public TrayContext()
    {
        _settings = Settings.Load();
        _client = new IngestClient(_settings);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open log", null, (_, _) => ShowLog());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Send test batch", null, async (_, _) => await SendTestBatchAsync());
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"Lushbdo Companion {UpdateChecker.Current.ToString(3)}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowLog();
        _icon.BalloonTipClicked += (_, _) => OpenReleasesPage();

        _log.Append($"Lushbdo Companion {UpdateChecker.Current.ToString(3)} started.");
        _log.Append(_settings.IsPaired
            ? $"Paired. Site: {_settings.BaseUrl}"
            : "Not paired yet — open Settings and paste a device token from the site's Devices page.");

        if (!_settings.IsPaired) ShowSettings();

        // Once at startup, then daily while running.
        _ = CheckForUpdatesAsync(manual: false);
        _updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
    }

    private void ShowLog()
    {
        _log.Show();
        _log.WindowState = FormWindowState.Normal;
        _log.Activate();
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() == DialogResult.OK)
            _log.Append(_settings.IsPaired ? $"Settings saved. Site: {_settings.BaseUrl}" : "Settings saved — still no token.");
    }

    private async Task SendTestBatchAsync()
    {
        var batch = IngestClient.TestBatch();
        _log.Append($"Sending test batch '{batch.BatchId}' ({batch.Lines.Count} lines) to {_settings.BaseUrl} …");
        ShowLog();

        var result = await _client.SendAsync(batch);
        if (!result.Ok || result.Answer is null)
        {
            _log.Append($"  failed: {result.Error}");
            return;
        }

        var answer = result.Answer;
        if (!answer.Applied)
        {
            _log.Append(answer.Reason == "no-session"
                ? "  the site has no running gather session — press Start on /gather and try again."
                : $"  not applied: {answer.Reason}");
            return;
        }

        if (answer.Session is { } s)
            _log.Append($"  landed on session {s.Id[..Math.Min(8, s.Id.Length)]} ({s.Items} items, running {s.ElapsedSec / 60}m).");
        foreach (var m in answer.Matched ?? [])
            _log.Append($"  matched  \"{m.LineText}\" → {m.Name}  +{m.Added} → {m.Qty}");
        foreach (var h in answer.Held ?? [])
            _log.Append($"  held     \"{h.LineText}\" ×{h.Count}  ({h.Why}) — resolve it on the session page.");
        foreach (var d in answer.Dropped ?? [])
            _log.Append($"  dropped  \"{d.LineText}\" ×{d.Count}  ({d.Why})");
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        var check = await UpdateChecker.RunAsync();

        if (check.UpdateAvailable)
        {
            _log.Append($"Update available: {check.LatestVersion} (you have {UpdateChecker.Current.ToString(3)}). Click the notification or the log's Releases link to download.");
            if (manual || !_updateBalloonShown)
            {
                _updateBalloonShown = true;
                _icon.ShowBalloonTip(10_000, "Lushbdo Companion",
                    $"Version {check.LatestVersion} is available — click to download.", ToolTipIcon.Info);
            }
            if (manual) OpenReleasesPage();
            return;
        }

        if (manual)
            _log.Append(check.Error is null ? "You are on the newest version." : $"Update check failed: {check.Error}");
    }

    private static void OpenReleasesPage() =>
        Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPage) { UseShellExecute = true });

    private void Quit()
    {
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }
}
