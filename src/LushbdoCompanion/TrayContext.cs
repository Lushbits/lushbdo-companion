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
    private readonly ToolStripMenuItem _traceItem;
    private readonly ToolStripMenuItem _windowsOcrItem;

    // All three rectangles live in one submenu and each says what it is set
    // to, because "which of these did I actually pick, and where?" was a
    // question the menu could not answer and the log could only answer at
    // startup.
    private readonly Dictionary<Settings.RegionKind, ToolStripMenuItem> _regionItems = [];
    private readonly Dictionary<Settings.RegionKind, ToolStripMenuItem> _forgetItems = [];
    private LootWatcher? _watcher;
    private LootSender? _sender;
    private bool _updateBalloonShown;

    public TrayContext()
    {
        _settings = Settings.Load();
        _client = new IngestClient(_settings);

        _watchItem = new ToolStripMenuItem("Start watching", null, async (_, _) => await ToggleWatchingAsync())
        {
            Enabled = _settings.RegionFor(Settings.RegionKind.Loot) is not null
        };

        _traceItem = new ToolStripMenuItem("Trace OCR to file", null, (_, _) => ToggleTrace())
        {
            Checked = _settings.TraceOcr
        };

        _windowsOcrItem = new ToolStripMenuItem("Read with Windows OCR (lighter, less accurate)", null, (_, _) => ToggleReader())
        {
            Checked = _settings.UseWindowsOcr
        };

        // One place for every rectangle, each showing what it is set to. The
        // loot log is the one the app cannot work without; the two balance
        // rectangles are independently optional and a member who never opens
        // the warehouse is never nagged for them (#22).
        var regions = new ToolStripMenuItem("Watched regions");
        foreach (var kind in RegionKinds)
        {
            var item = new ToolStripMenuItem("", null, async (_, _) => await PickRegionAsync(kind))
            {
                ToolTipText = kind == Settings.RegionKind.Loot
                    ? "Click to pick the loot chat rectangle. Drag it around the chat text."
                    : "Open the panel in-game first, then click. Drag around the silver figure and as " +
                      "little else — a neighbouring button inside the rectangle spoils the read.",
            };
            _regionItems[kind] = item;
            regions.DropDownItems.Add(item);
        }
        regions.DropDownItems.Add(new ToolStripSeparator());
        foreach (var kind in RegionKinds)
        {
            // Forgetting the loot log would just disable the app; the two
            // optional rectangles are the ones worth being able to drop, and
            // dropping a badly aimed one stops it spending passes on scenery.
            if (kind == Settings.RegionKind.Loot) continue;
            var item = new ToolStripMenuItem($"Forget {RegionName(kind).ToLowerInvariant()}", null,
                async (_, _) => await ForgetRegionAsync(kind));
            _forgetItems[kind] = item;
            regions.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip { ShowItemToolTips = true };
        menu.Items.Add("Open log", null, (_, _) => ShowLog());
        menu.Items.Add(regions);
        menu.Items.Add(_watchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open lushbdo.com", null, (_, _) => OpenSite());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Send test batch", null, async (_, _) => await SendTestBatchAsync());
        menu.Items.Add(_traceItem);
        menu.Items.Add(_windowsOcrItem);
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

        RefreshRegionMenu();

        _log.Append($"Lushbdo Companion {UpdateChecker.Current.ToString(3)} started.");
        _log.Append(_settings.IsPaired
            ? $"Paired. Site: {_settings.BaseUrl}"
            : "Not paired yet — open Settings and paste a device token from the site's Devices page.");
        LogRegions();

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

    private static readonly Settings.RegionKind[] RegionKinds =
        [Settings.RegionKind.Loot, Settings.RegionKind.Warehouse, Settings.RegionKind.Marketplace];

    /// <summary>
    /// Put every rectangle's current state on its own menu item — set or not,
    /// and exactly where. The pixels are there because they are the only way
    /// to tell two rectangles apart at a glance when one of them is aimed
    /// wrong, which is the thing that actually goes wrong (#22 field session).
    /// </summary>
    private void RefreshRegionMenu()
    {
        foreach (var kind in RegionKinds)
        {
            var rect = _settings.RegionFor(kind);
            _regionItems[kind].Text = rect is { } r
                ? $"{RegionName(kind)} — {r.Width}×{r.Height} at ({r.X}, {r.Y})"
                : $"{RegionName(kind)} — not picked yet";
            _regionItems[kind].Checked = rect is not null;
            if (_forgetItems.TryGetValue(kind, out var forget)) forget.Enabled = rect is not null;
        }
        _watchItem.Enabled = _settings.RegionFor(Settings.RegionKind.Loot) is not null;
    }

    /// <summary>The same state in the log, so a pasted log says what was watched.</summary>
    private void LogRegions()
    {
        if (_settings.RegionFor(Settings.RegionKind.Loot) is null)
        {
            _log.Append(_settings.HasScreenRelativeRegion
                ? "Capture is tied to the game window now, and the old screen-relative region cannot be carried " +
                  "over — right-click the tray icon → Watched regions → Loot log, once more."
                : "No loot log region yet — right-click the tray icon → Watched regions → Loot log, while the game " +
                  "shows its loot chat.");
        }
        foreach (var kind in RegionKinds)
        {
            if (_settings.RegionFor(kind) is not { } r) continue;
            _log.Append($"Region · {RegionName(kind)}: {r.Width}×{r.Height} at ({r.X}, {r.Y}) in the game window." +
                        (kind == Settings.RegionKind.Loot
                            ? " Right-click the tray icon → Start watching."
                            : " Read for your silver balance while that panel is open, and never sent."));
        }
    }

    /// <summary>
    /// What each rectangle is called, in the log, the menu and the picker. The
    /// loot log is the one the app cannot work without; the two balance
    /// rectangles are independently optional.
    /// </summary>
    private static string RegionName(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot => "Loot log",
        Settings.RegionKind.Warehouse => "Warehouse silver",
        _ => "Marketplace silver",
    };

    /// <summary>
    /// The picker's instruction. A balance rectangle has a failure the loot
    /// one does not: the still is the game *as it is right now*, so there is
    /// nothing to drag a rectangle around unless the panel was already open
    /// when the tray menu was used. That failure is silent and confusing, so
    /// the picker says it out loud (#22).
    /// </summary>
    private static string PickerHint(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot =>
            "This is a frozen frame of the game window — drag a rectangle around its loot chat tab. Esc cancels.",
        Settings.RegionKind.Warehouse =>
            "Open the warehouse in-game first. This is a frozen frame of the game window — drag a rectangle around " +
            "the warehouse's silver figure. If the warehouse is not in this picture, press Esc and pick again with " +
            "it open. Esc cancels.",
        _ =>
            "Open the central market in-game first. This is a frozen frame of the game window — drag a rectangle " +
            "around its silver figure. If the market is not in this picture, press Esc and pick again with it open. " +
            "Esc cancels.",
    };

    private static string LivePickerHint(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot => "Drag a rectangle around the game's loot chat tab — Esc cancels",
        Settings.RegionKind.Warehouse => "With the warehouse open, drag a rectangle around its silver figure — Esc cancels",
        _ => "With the central market open, drag a rectangle around its silver figure — Esc cancels",
    };

    /// <summary>
    /// Black Desert may close the warehouse or market panel when the game
    /// loses focus, and then the still has nothing to aim at. That is what the
    /// countdown picker is for — the existing pattern for "the game has to be
    /// in front", leaned on here rather than reinvented.
    /// </summary>
    private static bool AskToPickLive(Settings.RegionKind kind) =>
        MessageBox.Show(
            "Was the panel missing from that still?" + Environment.NewLine + Environment.NewLine +
            "The game may close it when you tab away. Pick on the live screen instead — you get three seconds to " +
            "switch to the game with the panel open." + Environment.NewLine + Environment.NewLine +
            $"Yes: pick {RegionName(kind).ToLowerInvariant()} on the live screen.   No: cancel.",
            "Lushbdo Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private async Task PickRegionAsync(Settings.RegionKind kind)
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
                using var picker = new FrozenRegionPickerForm(still, Screen.FromHandle(game.Hwnd).Bounds, PickerHint(kind));
                if (picker.ShowDialog() == DialogResult.OK)
                {
                    region = picker.Selection;
                }
                else if (kind == Settings.RegionKind.Loot || !AskToPickLive(kind))
                {
                    _log.Append($"{RegionName(kind)} region pick cancelled.");
                    return;
                }
                // Otherwise: the panel was not in the still, and the live
                // picker below is the way to catch it with the game in front.
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
            using var picker = new RegionPickerForm(LivePickerHint(kind));
            if (picker.ShowDialog() != DialogResult.OK)
            {
                _log.Append($"{RegionName(kind)} region pick cancelled.");
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

        _settings.SetRegion(kind, region.Value);
        _settings.Save();
        RefreshRegionMenu();
        _log.Append($"Region · {RegionName(kind)} set: {region.Value.Width}×{region.Value.Height} at ({region.Value.X}, {region.Value.Y}) in the game window.");
        if (!_watchItem.Enabled)
        {
            // One capture session serves every rectangle and it is aimed at
            // the loot log; silver rides along rather than watching on its own.
            _log.Append("Your silver will be read once a loot log region is picked too — one capture serves both, " +
                        "and it starts from the loot log.");
            return;
        }
        await StartWatchingAsync(); // picking a region is the intent to watch it
    }

    /// <summary>
    /// Drop one rectangle. Worth having per-region rather than all-or-nothing:
    /// a badly aimed one spends passes on scenery every time it goes still,
    /// and the answer to that should not be re-picking the one that works.
    /// </summary>
    private async Task ForgetRegionAsync(Settings.RegionKind kind)
    {
        if (_settings.RegionFor(kind) is null)
        {
            _log.Append($"{RegionName(kind)} is not set.");
            return;
        }
        var wasWatching = _watcher is not null;
        if (wasWatching) StopWatching($"Stopped watching while {RegionName(kind).ToLowerInvariant()} is dropped.");
        _settings.ForgetRegion(kind);
        _settings.Save();
        RefreshRegionMenu();
        _log.Append($"Region · {RegionName(kind)} forgotten — it is no longer read.");
        if (wasWatching) await StartWatchingAsync();
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
        if (_watcher is not null || _settings.RegionFor(Settings.RegionKind.Loot) is not { } region) return;

        LootSender? sender = null;
        if (_settings.IsPaired)
        {
            // Revoked fires on a worker thread; the menu and balloon live on
            // this one.
            var ui = SynchronizationContext.Current;
            sender = new LootSender(_client, msg =>
            {
                _log.Append(msg);
                _watcher?.TraceExternal(msg); // sender lines belong in a traced session too
            });
            sender.Revoked += why => ui?.Post(_ =>
            {
                StopWatching("Watching stopped — the site rejected this device's token. Pair again from the site's " +
                             "Devices page, then paste the new token in Settings.");
                _icon.ShowBalloonTip(10_000, "Lushbdo Companion",
                    "The site rejected this device's token — loot is no longer being sent.", ToolTipIcon.Warning);
            }, null);
        }
        else
        {
            _log.Append("Not paired — reading the loot log but sending nothing. Paste a device token in Settings to feed your sessions.");
        }

        var watcher = await StartWatcherAsync(region, sender);
        if (watcher is null)
        {
            sender?.Dispose();
            return;
        }

        _watcher = watcher;
        _sender = sender;
        if (_settings.TraceOcr) watcher.SetTracing(true);
        _watchItem.Text = "Stop watching";
        _log.Append("Watching the loot log. New pickups are confirmed across frames, then sent to your running gather " +
                    "session in small batches — start one on the site and play.");
        ShowLog();
    }

    /// <summary>
    /// Start on the preferred reader, and fall back to the OS one if it cannot
    /// run at all. PaddleOCR's ONNX Runtime links the *shared* Visual C++
    /// runtime — the redistributable Black Desert itself installs, so it is
    /// there on any machine that can run the game this app watches. "In
    /// practice" is not a thing to fail a member's session over, though, so a
    /// machine without it reads a little worse instead of not reading.
    /// </summary>
    private async Task<LootWatcher?> StartWatcherAsync(Rectangle region, LootSender? sender)
    {
        var order = _settings.UseWindowsOcr ? new[] { "windows" } : new[] { "paddle", "windows" };
        foreach (var which in order)
        {
            IOcrReader reader = which == "windows" ? new WindowsOcrReader() : new PaddleOcrReader();
            var watcher = new LootWatcher(region, _log.Append, sender is null ? null : sender.Add, reader: reader,
                balanceRegions: [.. _settings.BalanceRegions.Select(b => (
                    b.Kind == Settings.RegionKind.Warehouse ? BalanceBoard.Panel.Warehouse : BalanceBoard.Panel.Marketplace,
                    b.Rect))]);
            try
            {
                await watcher.StartAsync();
                return watcher;
            }
            catch (Exception e)
            {
                watcher.Dispose();
                if (which != "paddle")
                {
                    _log.Append($"Could not start watching: {e.Message}");
                    ShowLog();
                    return null;
                }
                _log.Append($"PaddleOCR could not start ({e.Message}) — reading with Windows OCR instead, which finds " +
                            "fewer rows. Installing the Microsoft Visual C++ 2015-2022 Redistributable (x64) is the " +
                            "usual fix; most machines already have it.");
            }
        }
        return null;
    }

    private void StopWatching(string message)
    {
        _watcher?.Dispose();
        _watcher = null;
        _sender?.Dispose();
        _sender = null;
        _watchItem.Text = "Start watching";
        _log.Append(message);
    }

    private void ToggleTrace()
    {
        _settings.TraceOcr = !_settings.TraceOcr;
        _settings.Save();
        _traceItem.Checked = _settings.TraceOcr;
        if (_watcher is not null) _watcher.SetTracing(_settings.TraceOcr);
        else _log.Append(_settings.TraceOcr ? "OCR trace will start with the next watch." : "OCR trace off.");
    }

    /// <summary>
    /// Swapping the recognizer changes what a pass costs and what it reads, so
    /// it takes effect on the next watch rather than mid-session — a reader
    /// changing under a board that is mid-consensus is a way to lose rows.
    /// </summary>
    private void ToggleReader()
    {
        _settings.UseWindowsOcr = !_settings.UseWindowsOcr;
        _settings.Save();
        _windowsOcrItem.Checked = _settings.UseWindowsOcr;
        var which = _settings.UseWindowsOcr ? "Windows OCR" : "PaddleOCR";
        _log.Append(_watcher is null
            ? $"Reading with {which} from the next watch."
            : $"Reading with {which} from the next watch — restart watching to switch now.");
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
        _sender?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Application.Exit();
    }
}
