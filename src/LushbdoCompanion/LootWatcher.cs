using System.Diagnostics;
using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The eyes, milestone (c) shape: region pixels arrive from an IFrameSource
/// cut out of the game window itself into a five-frame ring; OCR reads the
/// per-pixel median of that ring, never a raw frame, so static chat glyphs
/// stay sharp while the transparent background's animation smears away (#2).
/// "Did the pixels change" is no longer a meaningful gate — the world behind
/// the text always changes — so the gate moved up a level: OCR runs at half
/// the frame pace and only when the *stabilized* image changed; when it did
/// not, the previous pass's readings are reconfirmed to the board for free.
/// The LineBoard decides what is genuinely new and hands confirmed pickups to
/// the sender; nothing is ever sent on one frame's word. The source owns the
/// game window's lifecycle (waiting for it, re-finding it after a restart);
/// this class only reads — but a gap in frames means the game went away, so
/// what follows one is a fresh ring and a fresh baseline, never a median of
/// two different worlds.
/// </summary>
public sealed class LootWatcher : IDisposable
{
    /// <summary>~2 fps: a scrolling loot line is on screen for seconds; sampling twice a second cannot miss it.</summary>
    private static readonly TimeSpan FramePace = TimeSpan.FromMilliseconds(500);

    /// <summary>OCR considers running every other tick — reading at 1 Hz, which still gives a line many readings before it scrolls.</summary>
    private const int OcrEveryTicks = 2;

    /// <summary>Sampled mean-abs-diff below this and the stabilized image counts as unchanged.</summary>
    private const double StabilizedChangeGate = 3.0;

    /// <summary>This many frameless ticks (~5 s) means the game window is gone, not merely idle.</summary>
    private const int FrameGapTicks = 10;

    /// <summary>A quiet log line this often, so a silent log can only mean capture died.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(2);

    private readonly Rectangle _region; // window-relative physical pixels, from the region picker
    private readonly Action<string> _log;
    private readonly Action<string, int>? _onLoot;
    private readonly IFrameSource _source;
    private readonly FrameStabilizer _stabilizer = new();
    private readonly LineBoard _board;
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    private OcrEngine? _ocr;
    private int _scale;                // nearest-neighbour upscale — small chat text OCRs far better at 2×
    private SoftwareBitmap? _ocrInput; // allocated once, refilled per OCR pass
    private Size _ocrInputSize;
    private byte[] _lastOcrImage = [];
    private StreamWriter? _trace;      // opt-in diagnostics; null costs nothing
    private readonly object _traceLock = new();
    private string? _traceDir;
    private string? _tracePrefix;
    private int _traceDumps;
    private TextKeyer? _keyer;         // trace-only preview of the planned per-frame keying
    private byte[] _keyedDump = [];
    private const int TraceDumpEveryPasses = 20;  // one snapshot set ~every 20 s of activity
    private const int TraceDumpCap = 60;          // 3 PNGs per set; ≤ ~50 MB per session
    private long _framesCaptured;
    private long _ocrPasses;
    private long _pickups;
    private int _tick;
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
    /// Opt-in diagnostics: every OCR pass's raw lines and every board
    /// decision, written to a file so "did it see that row?" is a lookup,
    /// not an inference. Off, it costs one null check per event.
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

    private void TracePass(List<LineBoard.OcrLineInput> lines)
    {
        if (_trace is null) return;
        lock (_traceLock)
        {
            if (_trace is null) return;
            _trace.WriteLine($"{DateTime.Now:HH:mm:ss.f}  --- pass {_ocrPasses} — {lines.Count} line(s)");
            foreach (var l in lines)
                _trace.WriteLine($"             y={l.Y,6:F1} h={l.Height,4:F1}  \"{l.Text}\"");
        }
    }

    private void Log(string message)
    {
        _log(message);
        if (_trace is not null) Trace("log   " + message);
    }

    /// <summary>
    /// A periodic snapshot set — the median OCR reads today, the raw frame,
    /// and the raw frame keyed by <see cref="TextKeyer"/> — the ground truth
    /// for tuning per-frame keying against real scenes before it replaces
    /// the median in the OCR path. Trace-only, capped.
    /// </summary>
    private void MaybeDumpStabilized(RegionFrame frame)
    {
        if (_trace is null || _traceDumps >= TraceDumpCap || _ocrPasses % TraceDumpEveryPasses != 0) return;
        try
        {
            var w = _stabilizer.Width;
            var h = _stabilizer.Height;
            SavePng(_stabilizer.Stabilized, w, h, $"pass{_ocrPasses:D6}-median");
            SavePng(frame.Pixels, w, h, $"pass{_ocrPasses:D6}-raw");
            _keyer ??= new TextKeyer();
            if (_keyedDump.Length != w * h * 4) _keyedDump = new byte[w * h * 4];
            _keyer.Key(frame.Pixels, w, h, _keyedDump);
            SavePng(_keyedDump, w, h, $"pass{_ocrPasses:D6}-keyed");
            _traceDumps++;
            Trace($"dump  pass{_ocrPasses:D6} (median / raw / keyed)");
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
                    // The game went away and came back (restart, minimize).
                    // The ring must not median two different worlds, and
                    // whatever the chat shows now may already be counted.
                    _stabilizer.Clear();
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
                $" — stabilizing over {FrameStabilizer.Depth} frames before the first read.");
            _sinceLastLogged.Restart();
        }

        if (_stabilizer.Add(frame))
            _resetReason ??= "the watched region resized"; // everything visible next is old

        // OCR at half the frame pace: the ring smooths over 2.5 s anyway, and
        // a loot line is on screen for far longer than a second.
        if (++_tick % OcrEveryTicks != 0) return;

        // One OCR pass in flight, ever. The busy flag is also the lock around
        // the board and the OCR input bitmap.
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) != 0) return;
        var release = true;
        try
        {
            if (_resetReason is { } reason)
            {
                _resetReason = null;
                _board.Reset(reason);
            }
            if (!_stabilizer.Stabilize()) return; // ring still warming up

            var length = _stabilizer.Width * _stabilizer.Height * 4;
            if (_lastOcrImage.Length == length &&
                FrameStabilizer.MeanAbsDiff(_stabilizer.Stabilized, _lastOcrImage, length) < StabilizedChangeGate)
            {
                // The stabilized text did not change; the last readings hold
                // for another tick. This is what settles a line while the
                // scene is still — and what keeps an idle chat nearly free.
                if (_trace is not null) Trace("gate  stabilized image unchanged — reconfirming previous readings");
                _board.Reconfirm();
                return;
            }

            if (_lastOcrImage.Length != length) _lastOcrImage = new byte[length];
            _stabilizer.Stabilized.AsSpan(0, length).CopyTo(_lastOcrImage);
            MaybeDumpStabilized(frame);
            FillOcrInput();
            release = false; // RecognizeAsync owns the flag now
            _ = RecognizeAsync();
        }
        finally
        {
            if (release) Volatile.Write(ref _ocrBusy, 0);
        }
    }

    private unsafe void FillOcrInput()
    {
        var size = new Size(_stabilizer.Width, _stabilizer.Height);
        if (_ocrInput is null || size != _ocrInputSize)
        {
            _ocrInput?.Dispose();
            _ocrInput = new SoftwareBitmap(BitmapPixelFormat.Bgra8, size.Width * _scale, size.Height * _scale, BitmapAlphaMode.Ignore);
            _ocrInputSize = size;
        }

        using var buffer = _ocrInput.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        reference.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var dst, out _);
        var desc = buffer.GetPlaneDescription(0);

        fixed (byte* src = _stabilizer.Stabilized)
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

            // Lines go to the board with their vertical position in capture
            // pixels — position on the scroll stream is identity; the text
            // alone cannot be (identical lines repeat).
            var lines = new List<LineBoard.OcrLineInput>(result.Lines.Count);
            foreach (var line in result.Lines)
            {
                var text = line.Text.Trim();
                if (text.Length == 0 || line.Words.Count == 0) continue;
                double top = double.MaxValue, bottom = double.MinValue;
                foreach (var word in line.Words)
                {
                    top = Math.Min(top, word.BoundingRect.Y);
                    bottom = Math.Max(bottom, word.BoundingRect.Y + word.BoundingRect.Height);
                }
                lines.Add(new LineBoard.OcrLineInput(text, top / _scale, (bottom - top) / _scale));
            }
            TracePass(lines);
            _board.Ingest(lines);
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
