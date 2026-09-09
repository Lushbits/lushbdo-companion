using System.Diagnostics;

namespace LushbdoCompanion;

/// <summary>
/// The whole visible app: a tray icon, its menu, and the windows it opens.
/// There is deliberately no main window — the site is the product, this is the
/// typing you no longer do.
/// </summary>
public sealed class TrayContext : ApplicationContext, SettingsForm.IHost
{
    private readonly NotifyIcon _icon;
    private readonly Icon? _appIcon;
    private readonly Settings _settings;
    private readonly IngestClient _client;
    private readonly LogWindow _log = new();
    private readonly IconCache _icons;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly ToolStripMenuItem _watchItem;
    private LootWatcher? _watcher;
    private LootSender? _sender;
    private SilverSender? _silver;
    private OverlayForm? _overlay;
    private SettingsForm? _settingsWindow;
    private bool _previewing; // the settings window's Overlay page is open, and the overlay is its preview
    private bool _updateBalloonShown;

    public TrayContext()
    {
        _settings = Settings.Load();
        _client = new IngestClient(_settings);
        // The slots' icons (#52) outlive any one overlay window: fetched once
        // per path, kept for the run, and on disk for the next.
        _icons = new IconCache(_client, _log.Append);

        _watchItem = new ToolStripMenuItem("Start watching", null, async (_, _) => await ToggleWatchingAsync())
        {
            Enabled = CanWatch
        };

        // The shortest list the tray can be (#43): what is done in the moment.
        // Everything that is a setting — the pairing, the rectangles and
        // silver-only, the overlay, tracing and the update check — lives in
        // the settings window, with pages down its left, where each says what
        // it is set to and why it exists. The tray had grown too long to skim.
        var menu = new ContextMenuStrip();
        menu.Items.Add(_watchItem);
        menu.Items.Add("Open log", null, (_, _) => ShowLog());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _appIcon = AppIcon.Tray();
        _icon = new NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Application,
            Text = $"LushBDO Companion {UpdateChecker.Current.ToString(3)}",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowLog();
        _icon.BalloonTipClicked += (_, _) => OpenReleasesPage();

        _log.Append($"LushBDO Companion {UpdateChecker.Current.ToString(3)} started.");
        _log.Append(_settings.IsPaired
            ? $"Paired. Site: {_settings.BaseUrl}"
            : "Not paired yet — open Settings and paste a device token from the site's Devices page.");
        if (_settings.HadWindowsOcrPreference)
            _log.Append("You had \"Read with Windows OCR\" switched on. That option is gone — it read barely half " +
                        "the loot rows and could not read a silver balance at all. If you turned it on to save CPU, " +
                        "the setting that does that properly is \"Watch silver only\", which skips the loot log " +
                        "instead of reading it badly.");
        LogRegions();

        // A rectangle placement from before #43 is said again as an anchor and
        // an offset, at the size it drew. That size becomes a share of the
        // window's height, so the conversion wants the game window — or, with
        // the game not up, the monitor a borderless client fills.
        if (_settings.OverlayRegion is not null)
        {
            var height = GameWindow.Find()?.Bounds.Height ?? Screen.PrimaryScreen?.Bounds.Height ?? 1080;
            if (_settings.MigrateOverlayRegion(height))
            {
                _settings.Save();
                var o = _settings.Overlay;
                _log.Append($"Session overlay: the rectangle it was placed with is now an anchor ({o.Anchor}), an offset " +
                            $"({o.OffsetX}, {o.OffsetY}) and a text size ({o.TextPct:0.0} % of the window's height) — " +
                            "the same spot, and it now survives a resolution change. Settings → Overlay shows it live.");
            }
        }

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
        [Settings.RegionKind.Loot, Settings.RegionKind.Marketplace];

    /// <summary>The one thing on the tray that depends on the settings: whether there is anything to watch.</summary>
    private void RefreshWatchItem() => _watchItem.Enabled = CanWatch;

    /// <summary>
    /// What "Start watching" needs: in silver-only the balance rectangle is
    /// enough on its own, and the loot rectangle is not looked at.
    /// </summary>
    private bool CanWatch => _settings.SilverOnly
        ? _settings.BalanceRegion is not null
        : _settings.RegionFor(Settings.RegionKind.Loot) is not null;

    /// <summary>The same state in the log, so a pasted log says what was watched.</summary>
    private void LogRegions()
    {
        if (_settings.SilverOnly)
        {
            _log.Append(_settings.BalanceRegion is null
                ? "Silver only is on, but no silver rectangle is picked — Settings → Regions → Marketplace silver."
                : "Silver only is on — the loot log is not read at all.");
        }
        else if (_settings.RegionFor(Settings.RegionKind.Loot) is null)
        {
            _log.Append(_settings.HasScreenRelativeRegion
                ? "Capture is tied to the game window now, and the old screen-relative region cannot be carried " +
                  "over — Settings → Regions → Loot log, once more."
                : "No loot log region yet — Settings → Regions → Loot log, while the game shows its loot chat.");
        }
        foreach (var kind in RegionKinds)
        {
            if (_settings.RegionFor(kind) is not { } r) continue;
            _log.Append($"Region · {RegionName(kind)}: {r.Width}×{r.Height} at ({r.X}, {r.Y}) in the game window." +
                        (kind == Settings.RegionKind.Loot
                            ? " Right-click the tray icon → Start watching."
                            : " Read for your silver balance while that panel is open."));
        }
    }

    /// <summary>
    /// What each rectangle is called, in the log, the menu and the picker. The
    /// loot log is the one the app cannot work without; the balance rectangle
    /// is optional.
    /// </summary>
    private static string RegionName(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot => "Loot log",
        _ => "Marketplace silver",
    };

    /// <summary>
    /// The picker's instruction. A balance rectangle has a failure the loot
    /// one does not: the still is the game *as it is right now*, so there is
    /// nothing to drag a rectangle around unless the panel was already open
    /// when Pick was clicked. That failure is silent and confusing, so the
    /// picker says it out loud (#22).
    /// </summary>
    private static string PickerHint(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot =>
            "This is a frozen frame of the game window — drag a rectangle around its loot chat tab. Esc cancels.",
        _ =>
            "Open the Central Market in-game first. This is a frozen frame of the game window — drag a rectangle " +
            "around its Warehouse Balance figure, and keep any buttons out of it. If the market is not in this " +
            "picture, press Esc and pick again with it open. Esc cancels.",
    };

    private static string LivePickerHint(Settings.RegionKind kind) => kind switch
    {
        Settings.RegionKind.Loot => "Drag a rectangle around the game's loot chat tab — Esc cancels",
        _ => "With the Central Market open, drag a rectangle around its silver figure — Esc cancels",
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
            "LushBDO Companion", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    // --- What the settings window asks of the tray ------------------------

    public async Task PickRegionAsync(Settings.RegionKind kind)
    {
        var wasWatching = _watcher is not null;
        if (wasWatching) StopWatching("Stopped watching while the region is re-picked.");

        // The normal path: photograph one frame of the game's own window and
        // pick on that still. The game can sit buried under other windows —
        // the compositor serves its surface regardless, so there is no
        // arranging of windows before clicking Pick.
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
                else if (kind is Settings.RegionKind.Loot || !AskToPickLive(kind))
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
        RefreshWatchItem();
        _log.Append($"Region · {RegionName(kind)} set: {region.Value.Width}×{region.Value.Height} at ({region.Value.X}, {region.Value.Y}) in the game window.");
        if (!_watchItem.Enabled)
        {
            // Owner ruling (#22, 2026-08-30): watching is all or nothing.
            // Silver rides the loot log's capture and does not watch on its
            // own; stopping is what removing a region is for.
            _log.Append("Your silver will be read once a loot log region is picked too — watching is all or " +
                        "nothing, and one capture serves every rectangle.");
            return;
        }
        await StartWatchingAsync(); // picking a region is the intent to watch it
    }

    /// <summary>
    /// Drop one rectangle, either of the two. Per-region rather than
    /// all-or-nothing because a badly aimed one spends passes on scenery every
    /// time it goes still, and the answer to that should not be re-picking the
    /// one that works.
    ///
    /// The loot log is droppable too. Withholding it would have been the app
    /// deciding what a member is allowed to change about their own setup, and
    /// the consequence — watching stops until one is picked again — is theirs
    /// to weigh and is said plainly rather than prevented.
    ///
    /// It is also the documented way to stop watching one thing. Owner ruling
    /// (#22, 2026-08-30): watching is **all or nothing**, there is no
    /// silver-only mode and no per-region toggle, and removing the region is
    /// what turns a rectangle off. That closes the issue's open question about
    /// whether the balance should ride the same toggle — it does, and the
    /// Regions page is the whole of the control surface.
    /// </summary>
    public async Task ForgetRegionAsync(Settings.RegionKind kind)
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
        RefreshWatchItem();
        _log.Append($"Region · {RegionName(kind)} forgotten — it is no longer read.");

        if (kind == Settings.RegionKind.Loot)
        {
            // Owner ruling (#22, 2026-08-30): watching is all or nothing, and
            // dropping a region is how you stop watching it. So this is the
            // documented way out, not a gap.
            _log.Append("Watching is off until a loot log region is picked again — watching is all or nothing, " +
                        "and one capture serves every rectangle.");
            return;
        }
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
        if (_watcher is not null || !CanWatch) return;
        // In silver-only the loot rectangle is not watched even when it is set.
        var region = _settings.SilverOnly ? null : _settings.RegionFor(Settings.RegionKind.Loot);

        LootSender? sender = null;
        SilverSender? silver = null;
        if (_settings.IsPaired)
        {
            // Revoked fires on a worker thread; the menu and balloon live on
            // this one.
            var ui = SynchronizationContext.Current;
            void Say(string msg)
            {
                _log.Append(msg);
                _watcher?.TraceExternal(msg); // sender lines belong in a traced session too
            }
            void OnRevoked(string why) => ui?.Post(_ =>
            {
                StopWatching("Watching stopped — the site rejected this device's token. Pair again from the site's " +
                             "Devices page, then paste the new token in Settings.");
                _icon.ShowBalloonTip(10_000, "LushBDO Companion",
                    "The site rejected this device's token — nothing is being sent.", ToolTipIcon.Warning);
            }, null);

            if (region is not null)
            {
                sender = new LootSender(_client, Say);
                sender.Revoked += OnRevoked;
                sender.SessionSeen += session => ui?.Post(_ => _overlay?.Report(session), null);
            }

            // One credential opens both routes (bdo#668), so a revoked token
            // stops the balance the same way it stops the loot, and says so
            // once between them.
            if (_settings.BalanceRegion is not null)
            {
                silver = new SilverSender(_client, Say);
                silver.Revoked += OnRevoked;
            }
        }
        else
        {
            _log.Append("Not paired — reading the loot log but sending nothing. Paste a device token in Settings to feed your sessions.");
        }

        var watcher = await StartWatcherAsync(region, sender, silver);
        if (watcher is null)
        {
            sender?.Dispose();
            silver?.Dispose();
            return;
        }

        _watcher = watcher;
        _sender = sender;
        _silver = silver;
        if (_settings.TraceOcr) watcher.SetTracing(true);
        SyncOverlay();
        _watchItem.Text = "Stop watching";
        _log.Append(_settings.SilverOnly
            ? "Watching the silver balance only. The loot log is not read at all — no keying, no chat OCR — so this " +
              "costs a sampled diff over a small crop per tick and nothing else until a market panel is open."
            : "Watching the loot log. New pickups are confirmed across frames, then sent to your running gather " +
              "session in small batches — start one on the site and play.");
        if (_silver is not null)
            _log.Append("Your silver balance is sent too, whenever a confirmed figure differs from the one the site " +
                        "already has — no gather session needed for that one.");
        ShowLog();
    }

    /// <summary>
    /// One recognizer. There used to be a fallback to the OS one, and it was
    /// removed because it could not do the job: half the loot rows, and no
    /// silver at all. A machine that cannot run PaddleOCR now gets one sentence
    /// naming the fix instead of an app that quietly reads worse.
    /// </summary>
    private async Task<LootWatcher?> StartWatcherAsync(Rectangle? region, LootSender? sender, SilverSender? silver)
    {
        var watcher = new LootWatcher(region, _log.Append, sender is null ? null : sender.Add,
            reader: new PaddleOcrReader(),
            balance: _settings.BalanceRegion is { } balanceRect
                // Unpaired reads and logs and sends nothing; that is said here
                // rather than left as a null nobody notices.
                ? new LootWatcher.BalanceWatch(balanceRect, silver is null ? _ => { } : silver.Record)
                : null);
        try
        {
            await watcher.StartAsync();
            return watcher;
        }
        catch (Exception e)
        {
            watcher.Dispose();
            _log.Append($"Could not start watching: {e.Message}");
            // Only when the failure is actually about a missing native library.
            // Telling somebody on Windows 10 1909 to install a redistributable
            // is advice that cannot help them.
            if (LooksLikeMissingNativeLibrary(e))
                _log.Append("That is the native runtime PaddleOCR needs: install the Microsoft Visual C++ " +
                            "2015-2022 Redistributable (x64). Black Desert normally installs it, so this is rare.");
            ShowLog();
            return null;
        }
    }

    /// <summary>
    /// A DllNotFoundException, or a load failure carrying one underneath it —
    /// which is how a missing MSVCP140 surfaces through ONNX Runtime's own
    /// initialisation rather than as a clean throw at the P/Invoke boundary.
    /// </summary>
    private static bool LooksLikeMissingNativeLibrary(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is DllNotFoundException or BadImageFormatException) return true;
        return false;
    }

    /// <summary>
    /// The overlay exists exactly while there is a sender to feed it: it shows
    /// what the site reports back, so without a paired sender it has nothing
    /// honest to draw, and without watching it has nothing at all. The one
    /// other time it exists is as the settings window's preview (#43), which
    /// needs the real window to draw sample figures over the game with.
    /// </summary>
    private void SyncOverlay()
    {
        if (_previewing || (_settings.ShowOverlay && _sender is not null))
        {
            if (_overlay is null)
            {
                _overlay = new OverlayForm(_settings.Overlay, _settings.Slots, _icons);
                // A drag in a preview is a placement made on the game itself:
                // saved like one made on the page, and shown back on the page.
                _overlay.Placed += placement =>
                {
                    _settings.Overlay = placement;
                    _settings.Save();
                    _settingsWindow?.ShowPlacement(placement);
                };
                _overlay.SlotsPlaced += placement =>
                {
                    _settings.Slots = placement;
                    _settings.Save();
                    _settingsWindow?.ShowSlots(placement);
                };
            }
            return;
        }
        _overlay?.Dispose();
        _overlay = null;
    }

    public void PreviewOverlay(bool on)
    {
        _previewing = on;
        SyncOverlay();           // brings the window up for a preview, or takes it down after one
        _overlay?.Preview(on);   // and the one that stays goes back to the live figures
    }

    public void OverlayPlaced() => _overlay?.Place(_settings.Overlay);

    public void SlotsPlaced() => _overlay?.PlaceSlots(_settings.Slots);

    public void ClearIcons()
    {
        _icons.Clear();
        _log.Append("Cached item icons cleared — each slot's icon is fetched again the next time it is drawn.");
    }

    public void OverlayToggled()
    {
        SyncOverlay();
        if (!_settings.ShowOverlay)
        {
            _log.Append("Session overlay off.");
            return;
        }
        _log.Append(_sender is not null
            ? "Session overlay on — it appears over the game once the site reports a running session, and only while the game is in front."
            : _watcher is not null
                ? "Session overlay on — it needs the site's replies to draw from, so it appears once this device is paired and watching the loot log."
                : "Session overlay on — it appears once watching starts and the site reports a running session.");
    }

    public void PairingSaved() =>
        _log.Append(_settings.IsPaired ? $"Settings saved. Site: {_settings.BaseUrl}" : "Settings saved — still no token.");

    private void StopWatching(string message)
    {
        _watcher?.Dispose();
        _watcher = null;
        _sender?.Dispose();
        _sender = null;
        _silver?.Dispose();
        _silver = null;
        // No sender, no session: the overlay goes, unless it is mid-preview,
        // in which case it stays and just has no live figures to come back to.
        _overlay?.Report(null);
        SyncOverlay();
        _watchItem.Text = "Start watching";
        _log.Append(message);
    }

    /// <summary>
    /// The loot log is what costs CPU, so this is the setting that answers "it
    /// uses too much on my laptop" (16% was the field report). It takes effect
    /// at once rather than at the next watch, because a member who reaches for
    /// it wants the CPU back now.
    /// </summary>
    public async Task SilverOnlyChanged()
    {
        RefreshWatchItem();

        var wasWatching = _watcher is not null;
        if (wasWatching) StopWatching(_settings.SilverOnly
            ? "Switching to silver only — the loot log will not be read."
            : "Switching back to watching the loot log as well.");

        if (!CanWatch)
        {
            _log.Append(_settings.SilverOnly
                ? "Silver only is on, but no silver rectangle is picked — Settings → Regions → Marketplace silver."
                : "No loot log region is picked — Settings → Regions → Loot log.");
            return;
        }
        if (wasWatching) await StartWatchingAsync();
        else _log.Append(_settings.SilverOnly
            ? "Silver only is on. Start watching to read just the balance."
            : "Silver only is off. Start watching to read the loot log as well.");
    }

    public void TraceChanged()
    {
        if (_watcher is not null) _watcher.SetTracing(_settings.TraceOcr);
        else _log.Append(_settings.TraceOcr ? "OCR trace will start with the next watch." : "OCR trace off.");
    }

    public Task CheckForUpdatesAsync() => CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// One settings window, modeless: the log stays readable and the tray
    /// stays usable while it is open, and opening it again brings the one
    /// that is up to the page asked for. It disposes itself on close.
    /// </summary>
    private void ShowSettings(SettingsForm.Page page = SettingsForm.Page.Pairing)
    {
        if (_settingsWindow is { IsDisposed: false } open)
        {
            open.Open(page);
            open.Activate();
            return;
        }
        var form = new SettingsForm(_settings, this, page);
        form.FormClosed += (_, _) => _settingsWindow = null;
        _settingsWindow = form;
        form.Show();
    }

    public async Task SendTestBatchAsync()
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
            _log.Append(answer.Reason switch
            {
                "no-session" => "  the site has no running gather session — press Start on /gather and try again.",
                "paused" => "  the gather session is paused — press Resume on /gather and try again.",
                _ => $"  not applied: {answer.Reason}"
            });
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
                _icon.ShowBalloonTip(10_000, "LushBDO Companion",
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
        _settingsWindow?.Close(); // ends a preview, which is what would otherwise reach for the overlay below
        _overlay?.Dispose();
        _overlay = null;
        _icons.Dispose();
        _watcher?.Dispose();
        _sender?.Dispose();
        _silver?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon?.Dispose(); // NotifyIcon does not own the icon it was handed
        Application.Exit();
    }
}
