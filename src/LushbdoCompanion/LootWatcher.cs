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
/// change" per frame, which is what keeps an idle chat free whatever the
/// world behind it is doing, and <see cref="FrameDelta"/> takes it further
/// and answers "which rows changed" — so a pass reads the handful of rows
/// that are new rather than the whole region. The reading itself is
/// <see cref="IOcrReader"/>'s, and the default reader wants the raw frame
/// instead: PaddleOCR is a scene-text model and a transparent chat over a
/// moving world is its home ground, where it reads 963 of 1020 field rows
/// against Windows.Media.Ocr's 550.
///
/// A row still needs two clean reads before it counts, and they must be two
/// *different* frames — so the read window always reaches back over what
/// arrived last pass, and rows above it keep the readings they already had.
/// An unchanged keyed frame reconfirms all of them for free. OCR fragments
/// that share a visual row are merged left to right (the icon column splits
/// a row); the LineBoard sees whole rows, decides what is genuinely new, and
/// hands confirmed pickups to the sender. Nothing is ever sent on one
/// frame's word. The source owns the game window's lifecycle; a gap in
/// frames means the game went away, and what follows one is a fresh
/// baseline.
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
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    private int _frameWidth;
    private int _frameHeight;
    private byte[] _keyed = [];        // this frame's keyed text: the change gate, and what the delta reads
    private byte[] _lastKeyed = [];    // the previous OCR pass's keyed text
    private byte[] _raw = [];          // this frame's pixels as captured — the source reuses its own buffer
    private StreamWriter? _trace;      // opt-in diagnostics; null costs nothing
    private readonly object _traceLock = new();
    private string? _traceDir;
    private string? _tracePrefix;
    private int _traceDumps;
    private const int TraceDumpEveryPasses = 20;  // one snapshot set ~every 20 s of activity
    private const int TraceDumpCap = 60;          // 2 PNGs per set; ≤ ~50 MB per session
    private long _framesCaptured;
    private long _ocrPasses;
    private long _pickups;
    private int _nullTicks;
    private int _ocrBusy;
    private bool _announced;
    private volatile string? _resetReason;
    private string _lastFailure = "";
    private volatile bool _disposed;

    public LootWatcher(Rectangle region, Action<string> log, Action<string, int>? onLoot = null,
        IFrameSource? source = null, IOcrReader? reader = null)
    {
        _region = region;
        _log = log;
        _onLoot = onLoot;
        _source = source ?? new WgcFrameSource();
        _reader = reader ?? new PaddleOcrReader();
        _board = new LineBoard(OnConfirmedPickup, OnBoardNote, Trace);
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

        _source.Tick += OnTick;
        _source.Failed += OnFailed;
        _source.Status += Log;
        await _source.StartAsync(_region, FramePace);
    }

    private void OnTick(RegionFrame? tick)
    {
        try
        {
            if (_disposed) return;
            if (tick is { } frame)
            {
                if (_nullTicks >= FrameGapTicks && _framesCaptured > 0)
                {
                    // The game went away and came back (restart, minimize) —
                    // whatever the chat shows now may already be counted.
                    _resetReason = "the game window was gone for a while";
                }
                _nullTicks = 0;
                ReadFrame(frame);
            }
            else
            {
                _nullTicks++;
            }
            if (_sinceLastLogged.Elapsed >= HeartbeatEvery)
            {
                Log(_framesCaptured == 0
                    ? "Still here — no game window captured yet."
                    : $"Still watching — {_framesCaptured} frames, {_ocrPasses} OCR passes, {_pickups} pickups confirmed.");
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
                " — text keyed per frame, and only the rows that changed are read.");
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
            if (_trace is not null) Trace($"read  rows 0..{_frameHeight} of {_frameHeight}");

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
            var pieces = await _reader.ReadAsync(input, width, height, 0, height);
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
        }
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
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) == 0)
            _reader.Dispose(); // otherwise the in-flight pass finishes and the GC takes it
    }
}
