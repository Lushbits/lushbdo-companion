using System.Diagnostics;
using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The eyes: frames of the monitor holding the picked region arrive from an
/// IFrameSource, get cropped to that region, OCR'd by the offline Windows
/// engine, and every line is printed to the log. Milestone (b) deliberately
/// stops there — the same lines stay on screen across frames, so sending them
/// before milestone (c)'s scroll dedup would double-count massively.
/// </summary>
public sealed class LootWatcher : IDisposable
{
    /// <summary>~2 fps: a scrolling loot line is on screen for seconds, and OCR twice a second costs nothing.</summary>
    private static readonly TimeSpan FramePace = TimeSpan.FromMilliseconds(500);

    /// <summary>A quiet log line this often, so a silent log can only mean capture died.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromMinutes(2);

    private readonly Rectangle _region;
    private readonly Action<string> _log;
    private readonly IFrameSource _source;
    private readonly Stopwatch _sinceLastLogged = Stopwatch.StartNew();

    private OcrEngine? _ocr;
    private Rectangle _crop;   // the region, relative to its monitor's origin
    private int _scale;        // nearest-neighbour upscale — small chat text OCRs far better at 2×
    private long _framesRead;
    private bool _firstFrameAnnounced;
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
        _crop = Rectangle.Intersect(_region, monitorBounds);
        if (_crop.Width < 8 || _crop.Height < 8)
            throw new InvalidOperationException("the saved region is not on any monitor any more — pick it again.");
        if (_crop != _region)
            _log("The region hangs off its monitor's edge; watching the part that fits.");
        _crop.Offset(-monitorBounds.X, -monitorBounds.Y);

        var max = (int)OcrEngine.MaxImageDimension;
        if (_crop.Width > max || _crop.Height > max)
            throw new InvalidOperationException($"the region is bigger than OCR allows ({max}px) — drag it around just the loot chat.");
        _scale = _crop.Width * 2 <= max && _crop.Height * 2 <= max ? 2 : 1;

        _source.FrameArrived += OnFrame;
        _source.FrameFailed += OnFrameFailed;
        await _source.StartAsync(monitor, FramePace);
    }

    private async void OnFrame(SoftwareBitmap monitorPixels)
    {
        try
        {
            OcrResult result;
            using (monitorPixels)
            using (var lootChat = CropAndUpscale(monitorPixels, _crop, _scale))
                result = await _ocr!.RecognizeAsync(lootChat);
            if (_disposed) return;

            _framesRead++;
            if (!_firstFrameAnnounced)
            {
                _firstFrameAnnounced = true;
                _log($"Capture is live: {_crop.Width}×{_crop.Height}px region, OCR in {_ocr.RecognizerLanguage.DisplayName}" +
                     (_scale > 1 ? $" at {_scale}× upscale." : "."));
                _sinceLastLogged.Restart();
            }

            var lines = result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToArray();
            var frameText = string.Join("\n", lines);
            if (frameText != _lastFrameText)
            {
                // Something on screen moved. Print everything visible — repeats
                // across frames are expected and are exactly what milestone (c)
                // will dedup; this stage exists to enumerate real line shapes.
                _lastFrameText = frameText;
                foreach (var line in lines)
                    _log($"read  \"{line}\"");
                _sinceLastLogged.Restart();
            }
            else if (_sinceLastLogged.Elapsed >= HeartbeatEvery)
            {
                _log($"Still watching — {_framesRead} frames read so far, nothing new on screen.");
                _sinceLastLogged.Restart();
            }
        }
        catch (Exception e)
        {
            OnFrameFailed(e);
        }
    }

    private void OnFrameFailed(Exception e)
    {
        // One failure can repeat every frame; say it once, not twice a second.
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

    private static unsafe SoftwareBitmap CropAndUpscale(SoftwareBitmap source, Rectangle crop, int scale)
    {
        // Clamp every frame: a mode switch can shrink the monitor under us and
        // the pointer arithmetic below must never leave the source buffer.
        crop.Intersect(new Rectangle(0, 0, source.PixelWidth, source.PixelHeight));
        var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8,
            Math.Max(1, crop.Width * scale), Math.Max(1, crop.Height * scale), BitmapAlphaMode.Ignore);
        if (crop.Width < 1 || crop.Height < 1) return result; // off-screen: hand OCR a blank pixel

        using var src = source.LockBuffer(BitmapBufferAccessMode.Read);
        using var dst = result.LockBuffer(BitmapBufferAccessMode.Write);
        using var srcRef = src.CreateReference();
        using var dstRef = dst.CreateReference();
        srcRef.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var srcBytes, out _);
        dstRef.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var dstBytes, out _);
        var srcDesc = src.GetPlaneDescription(0);
        var dstDesc = dst.GetPlaneDescription(0);

        for (var y = 0; y < result.PixelHeight; y++)
        {
            var srcRow = (uint*)(srcBytes + srcDesc.StartIndex + (crop.Y + y / scale) * srcDesc.Stride) + crop.X;
            var dstRow = (uint*)(dstBytes + dstDesc.StartIndex + y * dstDesc.Stride);
            for (var x = 0; x < result.PixelWidth; x++)
                dstRow[x] = srcRow[x / scale];
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.FrameArrived -= OnFrame;
        _source.FrameFailed -= OnFrameFailed;
        _source.Dispose();
    }
}
