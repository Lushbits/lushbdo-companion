using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using LushbdoCompanion;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// The offline eval harness for #18: every pipeline variant reads the *same*
// trace-corpus images, and the report is counts, not vibes.
//
//   dotnet run --project src/LushbdoCompanion.Eval -- <trace-folder>
//
// Variants (all through Windows.Media.Ocr at 2× nearest-neighbour, exactly
// like the app): the median the app reads today, the raw frame, the keyed
// image as dumped by the session, and the keyed image recomputed from the
// raw by the current TextKeyer (so keyer changes are re-measurable against
// old corpora). OCR lines that share a row are merged left-to-right first —
// keying splits a row at the icon gap, and judging fragments as failures
// would be unfair.
//
// Metrics per variant: OCR lines after merging, rows whose text parses as
// loot grammar, and rows with an intact bracket pair. Ground truth beyond
// that is the owner's to state per #18; the full merged reads are printed
// so lines can be eyeballed and labeled.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: LushbdoCompanion.Eval <trace-folder>");
    return 1;
}

var folder = args[0];
var ocr = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
          ?? throw new InvalidOperationException("no en-US OCR language pack");
var keyer = new TextKeyer();
var totals = new Dictionary<string, (int Lines, int Loot, int Bracketed)>();

var rawFiles = Directory.GetFiles(folder, "*-raw.png").OrderBy(f => f).ToArray();
if (rawFiles.Length == 0)
{
    Console.Error.WriteLine($"no *-raw.png snapshots under {folder}");
    return 1;
}

foreach (var rawPath in rawFiles)
{
    var name = Path.GetFileName(rawPath);
    Console.WriteLine($"=== {name[..name.LastIndexOf('-')]}");

    var raw = LoadBgra(rawPath, out var w, out var h);
    var variants = new List<(string Name, byte[] Bgra)> { ("raw   ", raw) };

    var medianPath = rawPath.Replace("-raw.png", "-median.png");
    if (File.Exists(medianPath)) variants.Insert(0, ("median", LoadBgra(medianPath, out _, out _)));

    var keyedPath = rawPath.Replace("-raw.png", "-keyed.png");
    if (File.Exists(keyedPath)) variants.Add(("keyed ", LoadBgra(keyedPath, out _, out _)));

    var rekeyed = new byte[raw.Length];
    keyer.Key(raw, w, h, rekeyed);
    variants.Add(("rekey ", rekeyed));

    foreach (var (variantName, bgra) in variants)
    {
        var rows = await ReadRowsAsync(ocr, bgra, w, h);
        var loot = rows.Count(r => LootParser.Parse(r).Kind != LootParser.Kind.Unrecognized);
        var bracketed = rows.Count(r => r.Contains('[') && r.Contains(']'));
        totals.TryGetValue(variantName, out var acc);
        totals[variantName] = (acc.Lines + rows.Count, acc.Loot + loot, acc.Bracketed + bracketed);

        Console.WriteLine($"  {variantName}  rows {rows.Count,3}   loot-shaped {loot,3}   bracket-intact {bracketed,3}");
        foreach (var row in rows)
            Console.WriteLine($"      | {row}");
    }
    Console.WriteLine();
}

Console.WriteLine("=== totals across corpus");
foreach (var (variant, t) in totals)
    Console.WriteLine($"  {variant}  rows {t.Lines,4}   loot-shaped {t.Loot,4}   bracket-intact {t.Bracketed,4}");
return 0;

// One visual row can come back as several OCR lines (keying splits at the
// icon gap). Merge lines that share a vertical band, left to right — that
// is what the app will do too.
static async Task<List<string>> ReadRowsAsync(OcrEngine ocr, byte[] bgra, int w, int h)
{
    var scaled = Upscale2x(bgra, w, h);
    using var bmp = SoftwareBitmap.CreateCopyFromBuffer(scaled.AsBuffer(), BitmapPixelFormat.Bgra8, w * 2, h * 2, BitmapAlphaMode.Ignore);
    var result = await ocr.RecognizeAsync(bmp);

    var pieces = new List<(double X, double Y, double H, string Text)>();
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
        pieces.Add((x, top, bottom - top, text));
    }

    pieces.Sort((a, b) => a.Y.CompareTo(b.Y));
    var rows = new List<string>();
    for (var i = 0; i < pieces.Count;)
    {
        var bandY = pieces[i].Y;
        var bandH = Math.Max(pieces[i].H, 8);
        var band = new List<(double X, string Text)>();
        while (i < pieces.Count && pieces[i].Y < bandY + 0.6 * bandH)
        {
            band.Add((pieces[i].X, pieces[i].Text));
            i++;
        }
        band.Sort((a, b) => a.X.CompareTo(b.X));
        rows.Add(string.Join(' ', band.Select(p => p.Text)));
    }
    return rows;
}

static byte[] Upscale2x(byte[] bgra, int w, int h)
{
    var dst = new byte[w * 2 * h * 2 * 4];
    for (var y = 0; y < h * 2; y++)
    {
        var srcRow = y / 2 * w;
        var dstRow = y * w * 2;
        for (var x = 0; x < w * 2; x++)
        {
            var s = (srcRow + x / 2) * 4;
            var d = (dstRow + x) * 4;
            dst[d] = bgra[s];
            dst[d + 1] = bgra[s + 1];
            dst[d + 2] = bgra[s + 2];
            dst[d + 3] = 255;
        }
    }
    return dst;
}

static byte[] LoadBgra(string path, out int width, out int height)
{
    using var bmp = new Bitmap(path);
    width = bmp.Width;
    height = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var bytes = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * width * 4, width * 4);
    bmp.UnlockBits(data);
    return bytes;
}
