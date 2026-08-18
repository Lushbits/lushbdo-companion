using System.Diagnostics;
using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The eyes. Region pixels arrive from an IFrameSource cut out of the game
/// window itself; every frame is keyed by <see cref="TextKeyer"/> — the
/// game draws chat text as a bright core wrapped in a dark outline so it
/// reads over anything, and keying keeps exactly that structure and
/// flattens the animated world to black (#2, #18: measured 6× the readable
/// rows of the retired temporal median, which smeared text over text the
/// moment the chat scrolled). Each keyed frame is one crisp scroll state,
/// OCR reads every frame the keyed text actually changed — at the owner's
/// real loot pace (5–10 rows a second) a row lives ~2 s, and it needs two
/// clean reads before it counts — and an unchanged keyed frame reconfirms
/// the previous readings for free, which is what keeps an idle chat nearly
/// free. OCR fragments that share a visual row are merged left to right
/// (keying splits a row at the icon gap); the LineBoard sees whole rows,
/// decides what is genuinely new, and hands confirmed pickups to the
/// sender. Nothing is ever sent on one frame's word. The source owns the
/// game window's lifecycle; a gap in frames means the game went away, and
/// what follows one is a fresh baseline.
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
    private readonly LineBoard _board;
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    private OcrEngine? _ocr;
    private int _scale;                // nearest-neighbour upscale — small chat text OCRs far better at 2×
    private SoftwareBitmap? _ocrInput; // allocated once, refilled per OCR pass
    private int _frameWidth;
    private int _frameHeight;
    private byte[] _keyed = [];        // this frame's keyed text, the OCR input
    private byte[] _lastKeyed = [];    // the previous OCR pass's keyed text, the change gate
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

    public LootWatcher(Rectangle region, Action<string> log, Action<string, int>? onLoot = null, IFrameSource? source = null)
    {
        _region = region;
        _log = log;
        _onLoot = onLoot;
        _source = source ?? new WgcFrameSource();
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
        _ocr = CreateEnglishOcrEngine() ?? throw new InvalidOperationException(
            "Windows has no OCR language installed — add English (United States) under Settings → Time & language → Language.");

        var max = (int)OcrEngine.MaxImageDimension;
        if (_region.Width > max || _region.Height > max)
            throw new InvalidOperationException($"the region is bigger than OCR allows ({max}px) — drag it around just the loot chat.");
        _scale = _region.Width * 2 <= max && _region.Height * 2 <= max ? 2 : 1;

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
            Log($"Capture is live: {frame.Width}×{frame.Height}px region, OCR in {_ocr!.RecognizerLanguage.DisplayName}" +
                (_scale > 1 ? $" at {_scale}× upscale" : "") +
                " — text keyed per frame.");
            _sinceLastLogged.Restart();
        }

        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _keyed = new byte[_frameWidth * _frameHeight * 4];
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
            FillInput(_keyed, ref _ocrInput);
            release = false; // RecognizeAsync owns the flag now
            _ = RecognizeAsync();
        }
        finally
        {
            if (release) Volatile.Write(ref _ocrBusy, 0);
        }
    }

    private unsafe void FillInput(byte[] source, ref SoftwareBitmap? target)
    {
        var size = new Size(_frameWidth, _frameHeight);
        if (target is null || target.PixelWidth != size.Width * _scale || target.PixelHeight != size.Height * _scale)
        {
            target?.Dispose();
            target = new SoftwareBitmap(BitmapPixelFormat.Bgra8, size.Width * _scale, size.Height * _scale, BitmapAlphaMode.Ignore);
        }

        using var buffer = target.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        reference.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var dst, out _);
        var desc = buffer.GetPlaneDescription(0);

        fixed (byte* src = source)
        {
            var srcStride = size.Width * 4;
            for (var y = 0; y < size.Height * _scale; y++)
            {
                var srcRow = (uint*)(src + y / _scale * srcStride);
                var dstRow = (uint*)(dst + desc.StartIndex + y * desc.Stride);
                for (var x = 0; x < size.Width * _scale; x++)
                    dstRow[x] = srcRow[x / _scale];
            }
        }
    }

    private async Task RecognizeAsync()
    {
        try
        {
            var result = await _ocr!.RecognizeAsync(_ocrInput);
            _ocrPasses++;
            if (_disposed) return;

            // Fragments go through the row merge first (keying splits a row
            // at the icon gap), then to the board with their vertical
            // position in capture pixels — position on the scroll stream is
            // identity; the text alone cannot be (identical rows repeat).
            var pieces = new List<OcrRows.Piece>(result.Lines.Count);
            foreach (var line in result.Lines)
            {
                var text = line.Text.Trim();
                if (text.Length == 0 || line.Words.Count == 0) continue;
                double x = double.MaxValue, top = double.MaxValue, bottom = double.MinValue;
                foreach (var word in line.Words)
                {
                    x = Math.Min(x, word.BoundingRect.X);
                    top = Math.Min(top, word.BoundingRect.Y);
                    bottom = Math.Max(bottom, word.BoundingRect.Y + word.BoundingRect.Height);
                }
                pieces.Add(new OcrRows.Piece(x / _scale, top / _scale, (bottom - top) / _scale, text));
            }
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

    /// <summary>
    /// English client only for v1: prefer an English recognizer, fall back to
    /// whatever the profile offers rather than refusing to start.
    /// </summary>
    private static OcrEngine? CreateEnglishOcrEngine()
    {
        if (OcrEngine.TryCreateFromLanguage(new Language("en-US")) is { } enUs)
            return enUs;
        var english = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        if (english is not null && OcrEngine.TryCreateFromLanguage(english) is { } en)
            return en;
        return OcrEngine.TryCreateFromUserProfileLanguages();
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
            _ocrInput?.Dispose(); // otherwise the in-flight pass finishes and the GC takes it
    }
}
