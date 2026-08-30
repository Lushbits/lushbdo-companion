using RapidOcrNet;
using SkiaSharp;

namespace LushbdoCompanion;

/// <summary>
/// PaddleOCR PP-OCRv5 (latin) through ONNX Runtime on the CPU — detector,
/// then CRNN recognizer, no orientation pass (the chat is never upside down).
///
/// It reads the *raw* frame. That is the whole point of it: the chat
/// background is transparent by owner decision (#2), so every row is text over
/// a moving photograph, and a scene-text model is the one kind of recognizer
/// that was trained on exactly that. Shown the keyed frame instead it collapses
/// — 28% of names against 97% — because a hard-thresholded stroke is nothing
/// it has ever seen.
///
/// Cost is the price. Measured on the field corpus, a full 542×412 frame is
/// ~340 ms wall (~744 ms of one core) against Windows.Media.Ocr's 60 ms, and
/// roughly 70 ms of that is a fixed floor per call with ~16 ms per row on top.
/// That is why the watcher reads a strip rather than a frame: the rows above
/// the newest ones were already read, and re-reading them is the whole
/// difference between affordable and not.
/// </summary>
public sealed class PaddleOcrReader : IOcrReader
{
    public string Name => "PaddleOCR PP-OCRv5";
    public bool ReadsKeyed => false;
    public bool ReadsGroupedDigits => true;

    /// <summary>
    /// ONNX threads. Two, deliberately: the game is the foreground application
    /// and this is a background reader. Letting the runtime take every core
    /// finishes a pass sooner without lowering its total CPU, and a recognizer
    /// that fans out across a gaming machine's cores twice a second is the
    /// kind of thing players notice.
    /// </summary>
    private const int Threads = 2;

    private RapidOcr? _ocr;
    private RapidOcrOptions _options = RapidOcrOptions.Default;

    public Task StartAsync(int frameWidth, int frameHeight)
    {
        OcrModels.Unpack();
        var ocr = new RapidOcr();
        ocr.InitModels(
            OcrModels.Detector,
            OcrModels.Classifier,
            OcrModels.Recognizer,
            OcrModels.Dictionary,
            RapidOcr.GetDefaultSessionOptions(Threads));
        _ocr = ocr;
        // No 180° classifier — chat text is upright by construction — and no
        // scan border, which is a page-scanning affordance this never needs.
        _options = RapidOcrOptions.Default with { DoAngle = false, Padding = 0 };
        return Task.CompletedTask;
    }

    public async Task<List<OcrRows.Piece>> ReadAsync(byte[] bgra, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        var pixels = bitmap.GetPixels();
        var stride = width * 4;
        if (bitmap.RowBytes == stride)
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, pixels, height * stride);
        }
        else
        {
            // Skia is free to pad its rows, and a contiguous copy into a padded
            // bitmap shears the frame progressively — which OCR would still
            // read plausible-looking text out of, the one failure shape nothing
            // downstream could catch.
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * stride, pixels + y * bitmap.RowBytes, stride);
        }
        // Off the caller's thread: this is the capture source's timer callback,
        // and a few hundred milliseconds of inference on it is a stalled
        // capture. Windows.Media.Ocr was asynchronous for free; this one has to
        // be asked.
        var result = await _ocr!.DetectAsync(bitmap, _options);

        var pieces = new List<OcrRows.Piece>();
        foreach (var block in result.TextBlocks)
        {
            var text = block.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            float x = float.MaxValue, blockTop = float.MaxValue, blockBottom = float.MinValue;
            foreach (var point in block.BoxPoints)
            {
                x = Math.Min(x, point.X);
                blockTop = Math.Min(blockTop, point.Y);
                blockBottom = Math.Max(blockBottom, point.Y);
            }
            pieces.Add(new OcrRows.Piece(x, blockTop, blockBottom - blockTop, text));
        }
        return pieces;
    }

    public void Dispose() => _ocr?.Dispose();
}
