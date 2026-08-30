using System.Diagnostics;
using System.Drawing;

namespace LushbdoCompanion;

/// <summary>
/// The eyes. Region pixels arrive from an IFrameSource cut out of the game
/// window itself; every frame is keyed by <see cref="TextKeyer"/> — the
/// game draws chat text as a bright core wrapped in a dark outline so it
/// reads over anything, and keying keeps exactly that structure and
/// flattens the animated world to black (#2, #18).
///
/// Keying is now the *gate*, not the reading. It answers "did the text
/// change" per frame, and that one question is what keeps an idle chat free
/// whatever the world behind it is doing. The reading itself is
/// <see cref="IOcrReader"/>'s, and the default reader wants the raw frame
/// instead: PaddleOCR is a scene-text model and a transparent chat over a
/// moving world is its home ground, where it reads 963 of 1020 field rows
/// against Windows.Media.Ocr's 550.
///
/// Every pass reads the whole region. Reading only the rows that changed and
/// carrying the rest was tried and reverted — see the note at the read itself
/// for why it is unsound against this board. A row still needs two clean
/// reads before it counts, and an unchanged keyed frame reconfirms the
/// previous ones for free. OCR fragments that share a visual row are merged
/// left to right (the icon column splits a row); the LineBoard sees whole
/// rows, decides what is genuinely new, and hands confirmed pickups to the
/// sender. Nothing is ever sent on one
/// frame's word. The source owns the game window's lifecycle; a gap in
/// frames means the game went away, and what follows one is a fresh
/// baseline.
///
/// Since #22 the same capture also carries the silver-balance rectangle,
/// cropped off the same compositor frame and handed to
/// <see cref="BalanceBoard"/> — which gates on stillness rather than on keyed
/// change, for the reasons on that class. It rides this watcher rather than a
/// second capture session because a second session doubles the compositor's
/// work per tick, and it shares the one OCR slot rather than adding to it:
/// the loot region asks first, and the balance is read on the ticks the chat
/// did not need. A chat busy enough to want every pass is a chat nobody is
/// reading their market panel during.
/// </summary>
public sealed class LootWatcher : IDisposable
{
    /// <summary>~2 fps: at 5–10 rows/s a row crosses a screenful in seconds; every frame is a reading chance.</summary>
    private static readonly TimeSpan FramePace = TimeSpan.FromMilliseconds(500);

    /// <summary>Sampled mean-abs-diff below this and the keyed text counts as unchanged.</summary>
    private const double KeyedChangeGate = 1.5;

    /// <summary>This many frameless ticks (~5 s) means the game window is gone, not merely idle.</summary>
    private const int FrameGapTicks = 10;

    /// <summary>A quiet log line this often, so a silent log can only mean capture died.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(2);

    private readonly Rectangle _region; // window-relative physical pixels, from the region picker
    private readonly Action<string> _log;
    private readonly Action<string, int>? _onLoot;
    private readonly IFrameSource _source;
    private readonly TextKeyer _keyer = new();
    private readonly IOcrReader _reader;
    private readonly LineBoard _board;
    private readonly BalanceBoard _balance;
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    // The balance rectangle, as asked for and then as actually watched. It is
    // dropped rather than watched when the recognizer cannot hold grouped
    // digits — see IOcrReader.ReadsGroupedDigits.
    private readonly Rectangle? _balanceRequested;
    private bool _watchingBalance;
    private byte[] _balanceInput = [];   // the OCR input buffer for it
    private TextKeyer? _balanceKeyer;    // only if the reader asked for keyed pixels

    private int _frameWidth;
    private int _frameHeight;
    private byte[] _keyed = [];        // this frame's keyed text — the change gate
    private byte[] _lastKeyed = [];    // the previous OCR pass's keyed text
    private byte[] _raw = [];          // this frame's pixels as captured — the source reuses its own buffer
    private StreamWriter? _trace;      // opt-in diagnostics; null costs nothing
    private readonly object _traceLock = new();
    private string? _traceDir;
    private string? _tracePrefix;
    private int _traceDumps;
    private const int TraceDumpEveryPasses = 20;  // one snapshot set ~every 20 s of activity
    private const int TraceDumpCap = 60;          // 2 PNGs per set; ≤ ~50 MB per session
    private int _balanceDumps;
    private const int BalanceDumpCap = 40;        // one small PNG per balance read; the crops are tiny
    private long _framesCaptured;
    private long _ocrPasses;
    private long _pickups;
    private int _nullTicks;
    private int _ocrBusy;
    private int _readerDisposed;
    private bool _announced;
    private volatile string? _resetReason;
    private string _lastFailure = "";
    private volatile bool _disposed;

    public LootWatcher(Rectangle region, Action<string> log, Action<string, int>? onLoot = null,
        IFrameSource? source = null, IOcrReader? reader = null, Rectangle? balanceRegion = null,
        Action<long>? onBalance = null)
    {
        _region = region;
        _log = log;
        _onLoot = onLoot;
        _source = source ?? new WgcFrameSource();
        _reader = reader ?? new PaddleOcrReader();
        _board = new LineBoard(OnConfirmedPickup, OnBoardNote, Trace);
        _balance = new BalanceBoard(OnBoardNote, Trace, onBalance);
        _balanceRequested = balanceRegion;
    }

    /// <summary>
    /// Opt-in diagnostics: every OCR pass's rows and every board decision,
    /// written to a file so "did it see that row?" is a lookup, not an
    /// inference. Off, it costs one null check per event.
    /// </summary>
    public void SetTracing(bool on)
    {
        lock (_traceLock)
        {
            if (on == (_trace is not null)) return;
            if (!on)
            {
                _trace!.Dispose();
                _trace = null;
                _log("OCR trace stopped.");
                return;
            }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lushbdo-companion");
            Directory.CreateDirectory(dir);
            _traceDir = dir;
            _tracePrefix = $"trace-{DateTime.Now:yyyyMMdd-HHmmss}";
            _traceDumps = 0;
            var path = Path.Combine(dir, _tracePrefix + ".log");
            _trace = new StreamWriter(path) { AutoFlush = true };
            _log($"OCR trace started: {path} (plus periodic snapshots of what OCR reads)");
        }
    }

    private void Trace(string message)
    {
        if (_trace is null) return;
        lock (_traceLock)
        {
            _trace?.WriteLine($"{DateTime.Now:HH:mm:ss.f}  {message}");
        }
    }

    /// <summary>
    /// The fragments as the reader handed them over, before the row merge
    /// joins them. Without this, a row that arrives missing its item name is
    /// unanswerable from the trace — the merged line cannot say whether the
    /// reader never found the name or whether the merge put it on the wrong
    /// row, and those are opposite fixes (field trace 2026-08-24 22:14, where
    /// two rows came back as a bare "System You have obtained" and the log
    /// could not settle which).
    /// </summary>
    private void TracePieces(List<OcrRows.Piece> pieces)
    {
        if (_trace is null) return;
        lock (_traceLock)
        {
            if (_trace is null) return;
            _trace.WriteLine($"{DateTime.Now:HH:mm:ss.f}  piece {pieces.Count}");
            foreach (var p in pieces)
                _trace.WriteLine($"             x={p.X,6:F1} y={p.Y,6:F1} h={p.Height,5:F1}  \"{p.Text}\"");
        }
    }

    private void TracePass(List<LineBoard.OcrLineInput> rows)
    {
        if (_trace is null) return;
        lock (_traceLock)
        {
            if (_trace is null) return;
            _trace.WriteLine($"{DateTime.Now:HH:mm:ss.f}  --- pass {_ocrPasses} — {rows.Count} row(s)");
            foreach (var r in rows)
                _trace.WriteLine($"             y={r.Y,6:F1} h={r.Height,4:F1}  \"{r.Text}\"");
        }
    }

    private void Log(string message)
    {
        _log(message);
        if (_trace is not null) Trace("log   " + message);
    }

    /// <summary>Lets neighbors (the sender) land their log lines in the trace too, so a traced session is complete.</summary>
    public void TraceExternal(string message)
    {
        if (_trace is not null) Trace("log   " + message);
    }

    /// <summary>
    /// A periodic snapshot pair — the raw frame and what the keyer made of
    /// it, i.e. exactly what OCR read — so readability questions stay
    /// answerable from files, and every traced session grows the eval
    /// corpus (#18). Trace-only, capped.
    /// </summary>
    private void MaybeDumpFrames(RegionFrame frame)
    {
        if (_trace is null || _traceDumps >= TraceDumpCap || _ocrPasses % TraceDumpEveryPasses != 0) return;
        try
        {
            SavePng(frame.Pixels, _frameWidth, _frameHeight, $"pass{_ocrPasses:D6}-raw");
            SavePng(_keyed, _frameWidth, _frameHeight, $"pass{_ocrPasses:D6}-keyed");
            _traceDumps++;
            Trace($"dump  pass{_ocrPasses:D6} (raw / keyed)");
        }
        catch
        {
            // A failed snapshot must never take the watcher down.
        }
    }

    private unsafe void SavePng(byte[] bgra, int w, int h, string suffix)
    {
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        fixed (byte* src = bgra)
        {
            for (var y = 0; y < h; y++)
                Buffer.MemoryCopy(src + y * w * 4, (byte*)data.Scan0 + y * data.Stride, w * 4, w * 4);
        }
        bmp.UnlockBits(data);
        bmp.Save(Path.Combine(_traceDir!, $"{_tracePrefix}-{suffix}.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    public async Task StartAsync()
    {
        await _reader.StartAsync(_region.Width, _region.Height);

        // The balance rectangle rides the same capture, but only if this
        // recognizer can hold a grouped number at all. On one that cannot,
        // every strict-shape check would refuse and the passes would buy
        // nothing — so say so once instead of spending them.
        var balance = _balanceRequested;
        if (balance is not null && !_reader.ReadsGroupedDigits)
        {
            Log($"The silver balance region is not read by {_reader.Name} — it reads comma-grouped numbers as " +
                "letters (0 of 1,332 read correctly in the #18 bake-off), so nothing would ever confirm. Switch " +
                "off \"Read with Windows OCR\" to have your silver read.");
            balance = null;
        }
        _watchingBalance = balance is not null;
        if (_watchingBalance && _reader.ReadsKeyed) _balanceKeyer = new TextKeyer();

        _source.Tick += OnTick;
        _source.Failed += OnFailed;
        _source.Status += Log;
        // Slot 0 is the loot log; the balance rectangle is slot 1 when watched.
        await _source.StartAsync(balance is { } rect ? [_region, rect] : [_region], FramePace);
    }

    private void OnTick(FrameSet? tick)
    {
        try
        {
            if (_disposed) return;
            if (tick is { } set && set[0] is { } frame)
            {
                if (_nullTicks >= FrameGapTicks && _framesCaptured > 0)
                {
                    // The game went away and came back (restart, minimize) —
                    // whatever the chat shows now may already be counted, and
                    // whatever a balance panel was half-agreeing on is gone.
                    _resetReason = "the game window was gone for a while";
                    _balance.Reset("the game window was gone for a while");
                }
                _nullTicks = 0;
                ReadFrame(frame);
            }
            else
            {
                _nullTicks++;
            }

            // The balance rectangle asks second, and only ever gets the OCR
            // slot the chat left alone: the loot log is what this app is for.
            if (_watchingBalance && tick is { } crops && crops[1] is { } crop) ObserveBalance(crop);

            if (_sinceLastLogged.Elapsed >= HeartbeatEvery)
            {
                Log(_framesCaptured == 0
                    ? "Still here — no game window captured yet."
                    : $"Still watching — {_framesCaptured} frames, {_ocrPasses} OCR passes, {_pickups} pickups confirmed." +
                      (!_watchingBalance
                          ? ""
                          : $" Silver: {_balance.Reads} reads, {_balance.Confirmations} confirmed" +
                            (_balance.Confirmed is { } silver ? $", newest {BalanceParser.Money(silver)}." : ".")));
                _sinceLastLogged.Restart();
            }
        }
        catch (Exception e)
        {
            OnFailed(e);
        }
    }

    private void ReadFrame(RegionFrame frame)
    {
        _framesCaptured++;
        if (!_announced)
        {
            _announced = true;
            Log($"Capture is live: {frame.Width}×{frame.Height}px region, read by {_reader.Name}" +
                " — text keyed per frame, and read only when it changes.");
            if (_watchingBalance)
                Log("Also watching one rectangle for your silver balance, off the same capture — read only while " +
                    "the market panel is open and standing still, and never sent anywhere.");
            _sinceLastLogged.Restart();
        }

        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _keyed = new byte[_frameWidth * _frameHeight * 4];
            _raw = new byte[_keyed.Length];
            _lastKeyed = [];
            _resetReason ??= "the watched region resized"; // everything visible next is old
        }

        // One OCR pass in flight, ever. The busy flag is also the lock around
        // the board, the keyed buffers and the OCR input bitmap.
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) != 0) return;
        var release = true;
        try
        {
            if (_resetReason is { } reason)
            {
                _resetReason = null;
                _board.Reset(reason);
            }

            _keyer.Key(frame.Pixels, _frameWidth, _frameHeight, _keyed);

            var length = _keyed.Length;
            if (_lastKeyed.Length == length &&
                FrameStabilizer.MeanAbsDiff(_keyed, _lastKeyed, length) < KeyedChangeGate)
            {
                // The keyed text did not change; the last readings hold for
                // another tick. This settles a line while the scene is still
                // — and keeps an idle chat nearly free, whatever the world
                // behind it is doing.
                if (_trace is not null) Trace("gate  keyed text unchanged — reconfirming previous readings");
                _board.Reconfirm();
                return;
            }

            if (_lastKeyed.Length != length) _lastKeyed = new byte[length];
            _keyed.AsSpan(0, length).CopyTo(_lastKeyed);
            MaybeDumpFrames(frame);

            // The source reuses its pixel buffer between frames, so the raw
            // copy has to be taken before the read goes asynchronous.
            frame.Pixels.AsSpan(0, length).CopyTo(_raw);

            // The whole frame, every pass. Reading a strip and carrying the
            // rows above it is sound arithmetic and was measured to halve the
            // cost — but it is *not* sound against this board, and the field
            // said so within four minutes (2026-08-25 00:06): carried rows are
            // handed over already moved, so the board's text vote reads dy 0
            // however far the chat actually scrolled. Its provenance gate
            // authorises new lines only in proportion to that measurement, so
            // budget went to 0 and genuinely new pickups at the bottom edge
            // were never tracked. Twenty Black Gem Fragment, twenty Fairy
            // Powder and eight Fairy's Breath went missing from one eight
            // minute run.
            //
            // Making it sound means the board taking the shift as told rather
            // than voting it, and that trades a text measurement for a pixel
            // one on the path that decides how many new lines may exist —
            // where being wrong is a double count, the one outcome this app
            // may never produce. That is its own piece of work with its own
            // field proof, not a tuning change. FrameDelta stays for the eval
            // harness and for that work; the watcher reads everything.
            // The buffer and its shape are pinned here, while the flag is
            // held: a resize between now and the read landing would otherwise
            // hand the reader a freshly allocated frame mid-pass.
            release = false; // RecognizeAsync owns the flag now
            _ = RecognizeAsync(_reader.ReadsKeyed ? _keyed : _raw, _frameWidth, _frameHeight);
        }
        finally
        {
            if (release) Volatile.Write(ref _ocrBusy, 0);
        }
    }

    private async Task RecognizeAsync(byte[] input, int width, int height)
    {
        try
        {
            var pieces = await _reader.ReadAsync(input, width, height);
            _ocrPasses++;
            if (_disposed) return;
            TracePieces(pieces);

            // Fragments go through the row merge first (the icon column splits
            // a row), then to the board with their vertical position in
            // capture pixels — position on the scroll stream is identity; the
            // text alone cannot be (identical rows repeat).
            var rows = OcrRows.Merge(pieces);
            TracePass(rows);
            _board.Ingest(rows);
        }
        catch (Exception e)
        {
            OnFailed(e);
        }
        finally
        {
            Volatile.Write(ref _ocrBusy, 0);
            // Dispose could not take the reader while this pass held the flag.
            if (_disposed) DisposeReader();
        }
    }

    /// <summary>
    /// One balance rectangle's tick. The gate runs on every one of them and is
    /// deliberately the cheap half — a sampled diff over a digit-sized crop —
    /// so the steady state of "no panel open" costs arithmetic and no reading
    /// at all. A read is only ever taken on a tick the loot log did not want,
    /// and a picture it was not free for is simply looked at again next tick.
    /// </summary>
    private void ObserveBalance(RegionFrame crop)
    {
        var length = crop.Width * crop.Height * 4;
        if (!_balance.Observe(crop.Pixels, length)) return;

        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) != 0)
        {
            if (_trace is not null) Trace("bal   wanted a read, but the loot log has the reader");
            return;
        }
        var release = true;
        try
        {
            _balance.TakeRead();

            // Through the same seam as the loot path: the reader states which
            // buffer it wants and is handed that one. The source reuses its
            // pixel buffer between ticks, so the copy is taken before the read
            // goes asynchronous.
            if (_balanceInput.Length != length) _balanceInput = new byte[length];
            var input = _balanceInput;
            if (_balanceKeyer is { } keyer) keyer.Key(crop.Pixels, crop.Width, crop.Height, input);
            else crop.Pixels.AsSpan(0, length).CopyTo(input);

            MaybeDumpBalance(input, crop.Width, crop.Height);
            release = false; // RecognizeBalanceAsync owns the flag now
            _ = RecognizeBalanceAsync(input, crop.Width, crop.Height);
        }
        finally
        {
            if (release) Volatile.Write(ref _ocrBusy, 0);
        }
    }

    private async Task RecognizeBalanceAsync(byte[] input, int width, int height)
    {
        try
        {
            // tightCrop: the rectangle was drawn around the figure, so the
            // text runs to its edges and the detector needs its scan border to
            // find the row at all.
            var pieces = await _reader.ReadAsync(input, width, height, tightCrop: true);
            if (_disposed) return;

            // A crop this small can still come back as several fragments — a
            // `Silver` label above the figure, a coin glyph beside it. They are
            // merged the same way a chat row's fragments are and handed over as
            // one line; the strict shape is what decides whether there is a
            // number in it.
            var text = string.Join(' ', OcrRows.Merge(pieces).Select(r => r.Text));
            _balance.Ingest(text);
        }
        catch (Exception e)
        {
            OnFailed(e);
        }
        finally
        {
            Volatile.Write(ref _ocrBusy, 0);
            if (_disposed) DisposeReader();
        }
    }

    /// <summary>
    /// The crop exactly as the recognizer saw it, per read — a balance misread
    /// has to be answerable from files rather than inferred (#18), and these
    /// frames are what the eval harness scores on exact match. Trace-only,
    /// capped; the crops are small enough that every read can have one.
    /// </summary>
    private void MaybeDumpBalance(byte[] input, int width, int height)
    {
        if (_trace is null || _balanceDumps >= BalanceDumpCap) return;
        try
        {
            var buffer = _reader.ReadsKeyed ? "keyed" : "raw";
            SavePng(input, width, height, $"bal{_balanceDumps:D3}-{buffer}");
            _balanceDumps++;
        }
        catch
        {
            // A failed snapshot must never take the watcher down.
        }
    }

    /// <summary>
    /// Once, from whichever of Dispose and the last pass gets there second.
    /// The reader is not a managed buffer the GC will quietly reclaim: it owns
    /// ONNX inference sessions over native memory and the model weights they
    /// were built from, and re-picking the region mid-session stops a watcher
    /// while a pass is very much in flight.
    /// </summary>
    private void DisposeReader()
    {
        if (Interlocked.Exchange(ref _readerDisposed, 1) == 0) _reader.Dispose();
    }

    private void OnConfirmedPickup(string name, int count, string settledReading)
    {
        _pickups++;
        Log($"loot  \"{settledReading}\" → {name} ×{count}");
        _onLoot?.Invoke(name, count);
        _sinceLastLogged.Restart();
    }

    private void OnBoardNote(string message)
    {
        Log(message);
        _sinceLastLogged.Restart();
    }

    private void OnFailed(Exception e)
    {
        // One failure can repeat every tick; say it once, not twice a second.
        if (_disposed || e.Message == _lastFailure) return;
        _lastFailure = e.Message;
        Log($"A frame could not be read: {e.Message}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Tick -= OnTick;
        _source.Failed -= OnFailed;
        _source.Status -= Log;
        _source.Dispose();
        lock (_traceLock)
        {
            _trace?.Dispose();
            _trace = null;
        }
        // If a pass holds the flag it is mid-read and owns the reader; its
        // finally sees _disposed and hands it over.
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) == 0)
            DisposeReader();
    }
}
