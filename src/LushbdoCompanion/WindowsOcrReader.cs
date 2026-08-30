using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace LushbdoCompanion;

/// <summary>
/// The recognizer the app shipped through milestones (b) and (c): the OS's
/// own, reading the keyed frame at a nearest-neighbour upscale. Kept as the
/// fallback — it needs no model files and costs a fifth of PaddleOCR's CPU —
/// but it is no longer the default, for the reasons in <see cref="IOcrReader"/>.
/// </summary>
public sealed class WindowsOcrReader : IOcrReader
{
    public string Name => _ocr is null ? "Windows OCR" : $"Windows OCR ({_ocr.RecognizerLanguage.DisplayName})";
    public bool ReadsKeyed => true;

    // 0 of 1,332 comma-grouped numbers read correctly in the #18 bake-off.
    public bool ReadsGroupedDigits => false;

    private OcrEngine? _ocr;
    private int _scale;                // small chat text OCRs far better enlarged
    private SoftwareBitmap? _input;    // reallocated when either dimension changes
    private int _inputWidth;
    private int _inputHeight;

    public Task StartAsync(int frameWidth, int frameHeight)
    {
        _ocr = CreateEnglishOcrEngine() ?? throw new InvalidOperationException(
            "Windows has no OCR language installed — add English (United States) under Settings → Time & language → Language.");

        var max = (int)OcrEngine.MaxImageDimension;
        if (frameWidth > max || frameHeight > max)
            throw new InvalidOperationException($"the region is bigger than OCR allows ({max}px) — drag it around just the loot chat.");
        _scale = frameWidth * 2 <= max && frameHeight * 2 <= max ? 2 : 1;
        return Task.CompletedTask;
    }

    public async Task<List<OcrRows.Piece>> ReadAsync(byte[] bgra, int width, int height)
    {
        Fill(bgra, width, height);
        var result = await _ocr!.RecognizeAsync(_input);

        var pieces = new List<OcrRows.Piece>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0 || line.Words.Count == 0) continue;
            double x = double.MaxValue, lineTop = double.MaxValue, lineBottom = double.MinValue;
            foreach (var word in line.Words)
            {
                x = Math.Min(x, word.BoundingRect.X);
                lineTop = Math.Min(lineTop, word.BoundingRect.Y);
                lineBottom = Math.Max(lineBottom, word.BoundingRect.Y + word.BoundingRect.Height);
            }
            pieces.Add(new OcrRows.Piece(x / _scale, lineTop / _scale, (lineBottom - lineTop) / _scale, text));
        }
        return pieces;
    }

    private unsafe void Fill(byte[] source, int width, int height)
    {
        // Both dimensions, not just one: a region re-picked narrower but the
        // same height would otherwise keep a bitmap of the old width and every
        // row would be written past its stride.
        if (_input is null || _inputWidth != width || _inputHeight != height)
        {
            _input?.Dispose();
            _input = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width * _scale, height * _scale, BitmapAlphaMode.Ignore);
            _inputWidth = width;
            _inputHeight = height;
        }

        using var buffer = _input.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();
        reference.As<CaptureInterop.IMemoryBufferByteAccess>().GetBuffer(out var dst, out _);
        var desc = buffer.GetPlaneDescription(0);

        fixed (byte* src = source)
        {
            var srcStride = width * 4;
            for (var y = 0; y < height * _scale; y++)
            {
                var srcRow = (uint*)(src + y / _scale * srcStride);
                var dstRow = (uint*)(dst + desc.StartIndex + y * desc.Stride);
                for (var x = 0; x < width * _scale; x++)
                    dstRow[x] = srcRow[x / _scale];
            }
        }
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
        _input?.Dispose();
        _input = null;
    }
}
