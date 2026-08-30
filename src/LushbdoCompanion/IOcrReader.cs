namespace LushbdoCompanion;

/// <summary>
/// The recognizer, behind a seam. There is one implementation now, and the
/// seam stays for the reason it was introduced: the watcher takes a reader
/// rather than making one, so a future engine is a swap rather than surgery.
///
/// It used to have two, and the second one was Windows.Media.Ocr. That is gone
/// — not because PaddleOCR merely reads better, but because the OS recognizer
/// cannot do half the job at all. Measured over the 60-frame field corpus
/// (2026-08-22), scored through the site's own `item_name_key` fold so case
/// and confusable glyphs count as matches the way the register counts them:
///
///   Windows.Media.Ocr, keyed   550 of 1020 rows fully read,  88.6% names,  60 ms
///   PaddleOCR PP-OCRv5, raw    963 of 1020 rows fully read,  96.9% names, 337 ms
///
/// The gap is mostly not spelling — it is rows Windows.Media.Ocr cannot read
/// at all: it returned a closed bracket pair on 762 rows against PaddleOCR's
/// 1016. And on the silver balance it is worse than a gap: the same bake-off
/// has `Gold Bar I,OOOG` at 1,332 occurrences and **0 read correctly**, a
/// comma-grouped number with its digits read as letters, which is exactly what
/// a balance crop is. Kept as a fallback it was a switch that quietly halved
/// the loot read and turned silver off, offered in the tray as though it were
/// a preference.
///
/// So PaddleOCR is a hard requirement, and with it the Visual C++
/// 2015-2022 redistributable its ONNX Runtime links against. Black Desert
/// installs that, so a machine that can run the game this app watches has it;
/// a machine without it now gets one sentence naming the fix rather than an
/// app that silently reads worse. Every reader here is handed the raw captured
/// frame — the keyer stays, but only as the change gate it became.
/// </summary>
public interface IOcrReader : IDisposable
{
    /// <summary>For the log line that says what is reading.</summary>
    string Name { get; }

    /// <summary>
    /// Prepare the reader; throws with a member-readable reason if it cannot
    /// run at all — for PaddleOCR, models that will not unpack.
    ///
    /// It took the frame's dimensions until the OS recognizer left, because
    /// that one sized its upscale by them and refused a region past
    /// `OcrEngine.MaxImageDimension`. Nothing reads them now, and a parameter
    /// no implementation reads is a lie about what the seam needs.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Read the frame. Pieces come back positioned in capture pixels.
    ///
    /// <paramref name="tightCrop"/> says the text runs close to the edges
    /// because the rectangle was drawn *around* it — a balance crop, not a
    /// region that happens to contain text. It is not a preference: the
    /// detector's scan border is what gives it room to find a row near the
    /// boundary, and with the border off it silently drops that row. Measured
    /// on six field crops of a warehouse balance (2026-08-30), the same
    /// recognizer read the figure 0 times without the border and 6 times with
    /// it, returning only the label `Warehouse Balance` on every failure — a
    /// clean read of the wrong half of the picture, with nothing to mark it as
    /// incomplete.
    /// </summary>
    Task<List<OcrRows.Piece>> ReadAsync(byte[] bgra, int width, int height, bool tightCrop = false);
}
