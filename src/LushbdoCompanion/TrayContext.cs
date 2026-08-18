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
    private readonly ToolStripMenuItem _watchItem;
    private LootWatcher? _watcher;
    private bool _updateBalloonShown;

    public TrayContext()
    {
        _settings = Settings.Load();
        _client = new IngestClient(_settings);

        _watchItem = new ToolStripMenuItem("Start watching", null, async (_, _) => await ToggleWatchingAsync())
        {
            Enabled = _settings.Region is not null
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open log", null, (_, _) => ShowLog());
        menu.Items.Add("Pick loot log region…", null, async (_, _) => await PickRegionAsync());
        menu.Items.Add(_watchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open lushbdo.com", null, (_, _) => OpenSite());
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
        _log.Append(_settings.Region is { } region
            ? $"Loot log region saved: {region.Width}×{region.Height} at ({region.X}, {region.Y}) in the game window. Right-click the tray icon → Start watching."
            : _settings.HasScreenRelativeRegion
                ? "Capture is tied to the game window now, and the old screen-relative region cannot be carried over — right-click the tray icon → Pick loot log region once more."
                : "No loot log region yet — right-click the tray icon → Pick loot log region while the game shows its loot chat.");

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

    private async Task PickRegionAsync()
    {
        if (_watcher is not null) StopWatching("Stopped watching while the region is re-picked.");

        // The normal path: photograph one frame of the game's own window and
        // pick on that still. The game can sit buried under other windows —
        // the compositor serves its surface regardless, so there is no
        // arranging of windows before opening the tray menu.
        Rectangle? region = null;
        if (GameWindow.Find() is { } game)
        {
            try
            {
                using var still = await WgcFrameSource.CaptureStillAsync(game.Hwnd);
                using var picker = new FrozenRegionPickerForm(still, Screen.FromHandle(game.Hwnd).Bounds);
                if (picker.ShowDialog() != DialogResult.OK)
                {
                    _log.Append("Region pick cancelled.");
                    return;
                }
                region = picker.Selection;
            }
            catch (Exception e)
            {
                _log.Append($"Could not photograph the game window ({e.Message}) — picking on the live screen instead.");
            }
        }
        else
        {
            _log.Append($"The game window ({GameWindow.Description}) was not found — picking on the live screen instead.");
        }

        // Fallback: the live overlay, after a heads-up so the game can be
        // brought in front. The pick lands in screen pixels; anchoring it to
        // the game window still needs that window, so a pick made with no game
        // running cannot be saved — window capture has nothing else to aim at.
        if (region is null)
        {
            await ShowCountdownAsync();
            using var picker = new RegionPickerForm();
            if (picker.ShowDialog() != DialogResult.OK)
            {
                _log.Append("Region pick cancelled.");
                return;
            }
            if (GameWindow.Find() is not { } found)
            {
                _log.Append($"Nothing saved — the picked rectangle cannot be anchored to the game window while the game ({GameWindow.Description}) is not running. Start it and pick again.");
                ShowLog();
                return;
            }
            var screenRect = picker.Selection;
            var anchored = screenRect with { X = screenRect.X - found.Bounds.X, Y = screenRect.Y - found.Bounds.Y };
            if (!anchored.IntersectsWith(new Rectangle(Point.Empty, found.Bounds.Size)))
            {
                _log.Append("Nothing saved — the picked rectangle is not over the game window.");
                ShowLog();
                return;
            }
            region = anchored;
        }

        _settings.SetRegion(region.Value);
        _settings.Save();
        _watchItem.Enabled = true;
        _log.Append($"Loot log region set: {region.Value.Width}×{region.Value.Height} at ({region.Value.X}, {region.Value.Y}) in the game window.");
        await StartWatchingAsync(); // picking a region is the intent to watch it
    }

    private static async Task ShowCountdownAsync()
    {
        using var note = new CountdownForm();
        note.SetText("Switch to the game — picking in 3…");
        note.Show();
        for (var i = 3; i >= 1; i--)
        {
            note.SetText($"Switch to the game — picking in {i}…");
            await Task.Delay(1000);
        }
    }

    private async Task ToggleWatchingAsync()
    {
        if (_watcher is not null) StopWatching("Stopped watching.");
        else await StartWatchingAsync();
    }

    private async Task StartWatchingAsync()
    {
        if (_watcher is not null || _settings.Region is not { } region) return;

        var watcher = new LootWatcher(region, _log.Append);
        try
        {
            await watcher.StartAsync();
        }
        catch (Exception e)
        {
            watcher.Dispose();
            _log.Append($"Could not start watching: {e.Message}");
            ShowLog();
            return;
        }

        _watcher = watcher;
        _watchItem.Text = "Stop watching";
        _log.Append("Watching the loot log. Every line OCR reads is printed here; nothing is sent to the site yet — " +
                    "that needs milestone (c)'s dedup, because the same lines stay on screen across frames.");
        ShowLog();
    }

    private void StopWatching(string message)
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchItem.Text = "Start watching";
        _log.Append(message);
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

    private void OpenSite() =>
        Process.Start(new ProcessStartInfo(_settings.BaseUrl) { UseShellExecute = true });

    private void Quit()
    {
        _watcher?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }
}
