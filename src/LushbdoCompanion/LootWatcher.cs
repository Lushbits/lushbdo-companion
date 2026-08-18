using System.Diagnostics;
using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The eyes: region pixels arrive from an IFrameSource, get compared against
/// the previous frame, and only when they actually changed are they upscaled
/// and OCR'd — a static chat costs one vectorized memory compare per tick and
/// nothing else, which is what lets this sit beside a running game. Every
/// line read is printed to the log. Milestone (b) deliberately stops there —
/// the same lines stay on screen across frames, so sending them before
/// milestone (c)'s scroll dedup would double-count massively.
/// </summary>
public sealed class LootWatcher : IDisposable
{
    /// <summary>~2 fps: a scrolling loot line is on screen for seconds; sampling twice a second cannot miss it.</summary>
    private static readonly TimeSpan FramePace = TimeSpan.FromMilliseconds(500);

    /// <summary>A quiet log line this often, so a silent log can only mean capture died.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(2);

    private readonly Rectangle _region;
    private readonly Action<string> _log;
    private readonly IFrameSource _source;
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    private OcrEngine? _ocr;
    private int _scale;                // nearest-neighbour upscale — small chat text OCRs far better at 2×
    private SoftwareBitmap? _ocrInput; // allocated once, refilled per OCR pass
    private byte[] _prevPixels = [];
    private Size _frameSize;
    private long _framesCaptured;
    private long _ocrPasses;
    private int _ocrBusy;
    private bool _announced;
    private string _lastFrameText = "";
    private string _lastFailure = "";
    private volatile bool _disposed;

    public LootWatcher(Rectangle region, Action<string> log, IFrameSource? source = null)
    {
        _region = region;
        _log = log;
        _source = source ?? new WgcFrameSource();
    }

    public async Task StartAsync()
    {
        _ocr = CreateEnglishOcrEngine() ?? throw new InvalidOperationException(
            "Windows has no OCR language installed — add English (United States) under Settings → Time & language → Language.");

        var (monitor, monitorBounds) = CaptureInterop.MonitorFor(_region);
        var crop = Rectangle.Intersect(_region, monitorBounds);
        if (crop.Width < 8 || crop.Height < 8)
            throw new InvalidOperationException("the saved region is not on any monitor any more — pick it again.");
        if (crop != _region)
            _log("The region hangs off its monitor's edge; watching the part that fits.");
        crop.Offset(-monitorBounds.X, -monitorBounds.Y);

        var max = (int)OcrEngine.MaxImageDimension;
        if (crop.Width > max || crop.Height > max)
            throw new InvalidOperationException($"the region is bigger than OCR allows ({max}px) — drag it around just the loot chat.");
        _scale = crop.Width * 2 <= max && crop.Height * 2 <= max ? 2 : 1;

        _source.Tick += OnTick;
        _source.Failed += OnFailed;
        await _source.StartAsync(monitor, crop, FramePace);
    }

    private void OnTick(RegionFrame? tick)
    {
        try
        {
            if (_disposed) return;
            if (tick is { } frame) ReadFrame(frame);
            if (_sinceLastLogged.Elapsed >= HeartbeatEvery)
            {
                _log($"Still watching — {_framesCaptured} frames captured, {_ocrPasses} OCR passes, nothing new on screen.");
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
            _log($"Capture is live: {frame.Width}×{frame.Height}px region, OCR in {_ocr!.RecognizerLanguage.DisplayName}" +
                 (_scale > 1 ? $" at {_scale}× upscale." : "."));
            _sinceLastLogged.Restart();
        }

        // The gate that keeps this featherweight: OCR only runs when the chat
        // pixels actually changed.
        var size = new Size(frame.Width, frame.Height);
        var pixels = frame.Pixels.AsSpan(0, frame.Width * frame.Height * 4);
        if (size == _frameSize && pixels.SequenceEqual(_prevPixels)) return;

        // One OCR pass in flight, ever. A change landing mid-pass is not
        // recorded as seen, so the next tick picks it up.
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) != 0) return;

        if (size != _frameSize)
        {
            _frameSize = size;
            _prevPixels = new byte[pixels.Length];
            _ocrInput?.Dispose();
            _ocrInput = new SoftwareBitmap(BitmapPixelFormat.Bgra8, size.Width * _scale, size.Height * _scale, BitmapAlphaMode.Ignore);
        }
        pixels.CopyTo(_prevPixels);
        FillOcrInput(frame);
        _ = RecognizeAsync();
    }

    private unsafe void FillOcrInput(RegionFrame frame)
    {
        using var buffer = _ocrInput!.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        reference.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var dst, out _);
        var desc = buffer.GetPlaneDescription(0);

        fixed (byte* src = frame.Pixels)
        {
            var srcStride = frame.Width * 4;
            for (var y = 0; y < frame.Height * _scale; y++)
            {
                var srcRow = (uint*)(src + y / _scale * srcStride);
                var dstRow = (uint*)(dst + desc.StartIndex + y * desc.Stride);
                for (var x = 0; x < frame.Width * _scale; x++)
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

            var lines = result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToArray();
            var frameText = string.Join("\n", lines);
            if (frameText == _lastFrameText) return; // pixels moved but the text did not (animations, glow)
            // Something really changed. Print everything visible — repeats
            // across frames are expected and are exactly what milestone (c)
            // will dedup; this stage exists to enumerate real line shapes.
            _lastFrameText = frameText;
            foreach (var line in lines)
                _log($"read  \"{line}\"");
            _sinceLastLogged.Restart();
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

    private void OnFailed(Exception e)
    {
        // One failure can repeat every tick; say it once, not twice a second.
        if (_disposed || e.Message == _lastFailure) return;
        _lastFailure = e.Message;
        _log($"A frame could not be read: {e.Message}");
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
        _source.Dispose();
        if (Interlocked.CompareExchange(ref _ocrBusy, 1, 0) == 0)
            _ocrInput?.Dispose(); // otherwise the in-flight pass finishes and the GC takes it
    }
}
