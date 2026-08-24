namespace LushbdoCompanion;

/// <summary>
/// A recognizer, behind a seam, because #18's bake-off ended with two of them
/// worth keeping and one clear winner.
///
/// The two differ in what they want to be shown, which is the whole finding.
/// Windows.Media.Ocr needs the world taken away first — it reads the keyed
/// frame and falls apart on raw pixels (36% of names right against 89%).
/// PaddleOCR is a scene-text model: text over a photograph is its training
/// set, which is exactly what a transparent chat log is, and it wants the raw
/// frame — keyed, its hard-thresholded strokes are so far out of distribution
/// that it drops to 28%. So a reader states which buffer it reads and the
/// watcher hands it that one; the keyer keeps running regardless, because the
/// change gate that makes an idle chat free is built on it.
///
/// Measured over the 60-frame field corpus (2026-08-22), scored through the
/// site's own `item_name_key` fold so case and confusable glyphs count as
/// matches the way the register will count them:
///
///   Windows.Media.Ocr, keyed   550 of 1020 rows fully read,  88.6% names,  60 ms
///   PaddleOCR PP-OCRv5, raw    963 of 1020 rows fully read,  96.9% names, 337 ms
///
/// The gap is mostly not spelling — it is rows Windows.Media.Ocr cannot read
/// at all: it returned a closed bracket pair on 762 rows against PaddleOCR's
/// 1016.
/// </summary>
public interface IOcrReader : IDisposable
{
    /// <summary>For the log line that says what is reading.</summary>
    string Name { get; }

    /// <summary>
    /// True when this reader is shown the keyer's output, false when it reads
    /// the captured pixels as they came.
    /// </summary>
    bool ReadsKeyed { get; }

    /// <summary>
    /// Prepare the reader; throws with a member-readable reason if it cannot
    /// run at all (no language pack, no models).
    /// </summary>
    Task StartAsync(int frameWidth, int frameHeight);

    /// <summary>
    /// Read rows <paramref name="top"/> (inclusive) to <paramref name="bottom"/>
    /// (exclusive) of the frame. Pieces come back positioned against the whole
    /// frame, not the window, so the caller never has to add the offset back.
    /// </summary>
    Task<List<OcrRows.Piece>> ReadAsync(byte[] bgra, int width, int height, int top, int bottom);
}
