using System.Text;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using LushbdoCompanion;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using RapidOcrNet;
using SkiaSharp;

// The offline eval harness for #18: every pipeline variant reads the *same*
// trace-corpus images, and the report is counts, not vibes.
//
//   dotnet run --project src/LushbdoCompanion.Eval -- <trace-folder> [options]
//     --vocab <file>     known-good item names, one per line (# comments)
//     --variants <csv>   subset of the variant table below (default: all)
//     --frames <n>       read only the first n frames
//     --rows             print every merged read, not just the misses
//     --tolerant         strip the chat tag box before parsing, so a variant is
//                        judged on its reading and not on the parser's
//                        "verb at position 0" rule
//     --balance          score the silver-balance crops (#22) instead of the
//                        loot frames: every *-warehouse-*.png / *-marketplace-*.png
//                        snapshot, read by each engine on the pipeline it
//                        actually ships with, through the app's own strict
//                        shape. Scored on **exact match**, not through the
//                        item_name_key fold — that fold is about confusable
//                        letters and means nothing for a number, where a
//                        single wrong digit is the whole failure.
//     --expect <n>       the true balance in those crops, for --balance to
//                        score against. Without it the readings are printed
//                        and nothing is scored.
//     --engine win|rapid|both   which recognizer reads the variants. `win` is
//                        Windows.Media.Ocr, what the app ships. `rapid` is
//                        PaddleOCR PP-OCRv5 (latin) through ONNX Runtime — the
//                        candidate engine #18 held open.
//
// A variant is a preprocessing recipe fed to Windows.Media.Ocr: how the
// keyer decides text, and how the keyed image is enlarged for the
// recognizer. OCR lines that share a row are merged left-to-right first —
// keying splits a row at the icon gap, and judging fragments as failures
// would be unfair.
//
// Scoring is done through the *site's* matcher, not through string equality:
// bdo's `item_name_key` lowercases, collapses every run of non-alphanumerics
// to one space, and folds the glyph pairs a screen font makes interchangeable
// (0/o, 1/l/i, 5/s, 2/z, 4/a, 6/b, 8/b, 9/g). So `Magnetite ore` and
// `Gold Bar I,OOOG` already land on the right item and are not errors here.
// Judging on exact spelling overstates every engine's error rate; judging on
// the key is what predicts what the register will actually file.
//
// Scoring. "loot-shaped" is the grammar gate the app already applies. With
// a vocabulary it goes further and asks the only question the product
// cares about: is the bracketed name *exactly* a real item? That is what
// separates a mangle the board's voting will discard from a mangle it will
// confidently send — the systematic misread that repeats every frame
// (`Ancient. Spirit. Oust`, `Gold Bar I,OOOG`) is invisible to a
// loot-shaped count and fatal downstream. Names outside the vocabulary are
// listed by frequency so a new corpus's unknowns can be curated in.

var folder = "";
string? vocabPath = null;
string? only = null;
var maxFrames = int.MaxValue;
var printRows = false;
var tolerant = false;
var simulate = false;
var balance = false;
long? expected = null;
var engines = new[] { "win" };
var rapidPreset = "default";
var rapidThreads = 0;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--vocab": vocabPath = args[++i]; break;
        case "--variants": only = args[++i]; break;
        case "--frames": maxFrames = int.Parse(args[++i]); break;
        case "--rows": printRows = true; break;
        case "--simulate": simulate = true; break;
        case "--balance": balance = true; break;
        case "--expect": expected = long.Parse(args[++i].Replace(",", "").Replace(".", "")); break;
        case "--tolerant": tolerant = true; break;
        case "--rapid-opts": rapidPreset = args[++i]; break;
        case "--rapid-threads": rapidThreads = int.Parse(args[++i]); break;
        case "--engine":
            var chosen = args[++i];
            engines = chosen == "both" ? ["win", "rapid"] : [chosen];
            break;
        default: folder = args[i]; break;
    }
}
if (folder.Length == 0)
{
    Console.Error.WriteLine("usage: LushbdoCompanion.Eval <trace-folder> [--vocab f] [--variants a,b] [--frames n] [--rows]");
    return 1;
}

HashSet<string>? vocab = null;
if (vocabPath is not null)
{
    vocab = new HashSet<string>(StringComparer.Ordinal);
    foreach (var raw in File.ReadAllLines(vocabPath))
    {
        var line = raw.Trim();
        if (line.Length > 0 && !line.StartsWith('#')) vocab.Add(ItemNameKey(line));
    }
}

// The variant table. `cur` is what the app ships today; everything else is a
// single-axis change from it so a win is attributable.
var table = new List<Variant>
{
    new("cur",       new Key(140, 80, 2, Out.Brightness), 2, Smooth: false),
    new("x3",        new Key(140, 80, 2, Out.Brightness), 3, Smooth: false),
    new("x3norm",    new Key(140, 80, 2, Out.Normalized), 3, Smooth: false),
    new("x1",        new Key(140, 80, 2, Out.Brightness), 1, Smooth: false),
    new("x2inv",     new Key(140, 80, 2, Out.Brightness), 2, Smooth: false, Invert: true),
    new("x3inv",     new Key(140, 80, 2, Out.Brightness), 3, Smooth: false, Invert: true),
    new("x3norminv", new Key(140, 80, 2, Out.Normalized), 3, Smooth: false, Invert: true),
    new("raw",       null,                                1, Smooth: false),
    new("raw2",      null,                                2, Smooth: false),
    new("raw3",      null,                                3, Smooth: false),
    new("raw2s",     null,                                2, Smooth: true),
};
if (only is not null)
{
    var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
    table.RemoveAll(v => !wanted.Contains(v.Name));
}

var winOcr = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
             ?? throw new InvalidOperationException("no en-US OCR language pack");
RapidOcr? rapidOcr = null;
if (engines.Contains("rapid"))
{
    rapidOcr = new RapidOcr();
    // dotnet run leaves the cwd at the project, so name the bundled models
    // by the binary's own folder rather than relatively.
    var m = Path.Combine(AppContext.BaseDirectory, "models", "v5");
    rapidOcr.InitModels(
        Path.Combine(m, "ch_PP-OCRv5_mobile_det.onnx"),
        Path.Combine(m, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
        Path.Combine(m, "latin_PP-OCRv5_rec_mobile_infer.onnx"),
        Path.Combine(m, "ppocrv5_latin_dict.txt"),
        RapidOcr.GetDefaultSessionOptions(rapidThreads));
}

if (balance)
{
    // The balance crops (#22) are the ones the watcher tagged `-bal` — a
    // separate corpus in the same folder, and one the loot sweep below skips
    // for the same reason: a rectangle round four digits says nothing about
    // reading chat rows.
    var crops = Directory.GetFiles(folder, "*-bal*.png").OrderBy(f => f).Take(maxFrames).ToArray();
    if (crops.Length == 0)
    {
        Console.Error.WriteLine($"no *-bal*.png balance snapshots under {folder}");
        return 1;
    }

    // Each engine reads the crop the way it reads everything else in the app:
    // PaddleOCR the raw pixels at 1:1, Windows.Media.Ocr the keyed frame at
    // the 2× enlargement it ships with. Anything else measures a pipeline
    // nothing runs.
    //
    // For PaddleOCR that means the *padded* recipe, which is what the app uses
    // for a tight crop — `--rapid-opts tight` reproduces the unpadded one the
    // chat region gets, and is how the border's worth was measured (0 of 6
    // field crops read against 6 of 6). These two silently disagreed once
    // already: this harness defaulted to padded while the app ran unpadded, so
    // a green report was scoring a pipeline nothing shipped.
    var shippedKey = new Key(140, 80, 2, Out.Brightness);
    var tally = new Dictionary<string, BalanceScore>();
    foreach (var name in engines) tally[name] = default;

    foreach (var path in crops)
    {
        Console.WriteLine($"=== {Path.GetFileName(path)}");
        var pixels = LoadBgra(path, out var w, out var h);
        foreach (var engine in engines)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var rows = engine == "rapid"
                ? ReadRowsRapid(rapidOcr!, pixels, w, h, 1, false, rapidPreset)
                : await ReadRowsAsync(winOcr, shippedKey.Apply(pixels, w, h), w, h, 2, false);
            var elapsed = clock.Elapsed.TotalMilliseconds;

            var text = string.Join(' ', rows);
            var reading = BalanceParser.Parse(text);
            var score = tally[engine];
            score.Crops++;
            score.Millis += elapsed;

            string verdict;
            if (!reading.Ok)
            {
                score.Refused++;
                verdict = $"refused ({reading.Why})";
            }
            else
            {
                score.Shaped++;
                // Exact match, deliberately: the item_name_key fold this
                // harness scores loot through is about confusable letters and
                // means nothing for a number, where one wrong digit is the
                // entire failure.
                if (expected is not { } want) verdict = "read";
                else if (reading.Value == want) { score.Exact++; verdict = "EXACT"; }
                else { score.Wrong++; verdict = $"WRONG (expected {BalanceParser.Money(want)})"; }
            }
            tally[engine] = score;
            Console.WriteLine($"  {engine,-6} {elapsed,5:F0} ms  \"{text}\"  ->  " +
                              (reading.Ok ? BalanceParser.Money(reading.Value) : "—") + $"   {verdict}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== balance totals across {crops.Length} crop(s)" +
                      (expected is { } truth ? $", expecting {BalanceParser.Money(truth)}" : ", nothing to score against"));
    Console.WriteLine($"  {"engine",-8}{"crops",7}{"shaped",8}{"refused",9}{"exact",7}{"wrong",7}   ms/crop");
    foreach (var engine in engines)
    {
        var t = tally[engine];
        Console.WriteLine($"  {engine,-8}{t.Crops,7}{t.Shaped,8}{t.Refused,9}{t.Exact,7}{t.Wrong,7}   " +
                          $"{t.Millis / Math.Max(t.Crops, 1),7:F0}");
    }
    // A refusal is the safe direction and a column is enough for it. A figure
    // that passed the strict shape and is still wrong is the one outcome this
    // feature may never produce, so it gets said out loud.
    foreach (var engine in engines)
        if (tally[engine].Wrong > 0)
            Console.WriteLine($"  !! {engine} produced {tally[engine].Wrong} shape-valid but WRONG figure(s) — the " +
                              "strict shape did not catch them, and nothing downstream would either.");
    return 0;
}

var rawFiles = Directory.GetFiles(folder, "*-raw.png")
    .Where(f => !Path.GetFileName(f).Contains("-bal"))
    .OrderBy(f => f).Take(maxFrames).ToArray();
if (rawFiles.Length == 0)
{
    Console.Error.WriteLine($"no *-raw.png snapshots under {folder}");
    return 1;
}

if (simulate)
{
    // What the shipped pipeline will actually do, on real pixels: key the
    // frame, scroll it the way a pass of loot scrolls it, and ask FrameDelta
    // what still has to be read. The corpus is 20 passes apart, so a
    // consecutive pair is synthesised — frame N scrolled up by one pass's
    // worth, with frame N+1's bottom rows as the new arrivals.
    var keyer = new TextKeyer();
    var delta = new FrameDelta();
    var files = rawFiles;
    double fullMs = 0, windowMs = 0;
    int whole = 0, passes = 0, rowsRead = 0, rowsTotal = 0;
    for (var i = 0; i + 1 < files.Length; i++)
    {
        var a = LoadBgra(files[i], out var w, out var h);
        var b = LoadBgra(files[i + 1], out _, out _);
        const int Pitch = 24;
        var scroll = 3 * Pitch;                 // ~6 rows a second at 2 fps
        var next = ScrollUp(a, b, w, h, scroll);

        var keyedA = new byte[a.Length];
        var keyedB = new byte[a.Length];
        keyer.Key(a, w, h, keyedA);
        keyer.Key(next, w, h, keyedB);

        delta.Reset();
        delta.Compare(keyedA, w, h, Pitch);
        var window = delta.Compare(keyedB, w, h, Pitch);
        passes++;
        if (window.Whole) whole++;
        rowsRead += h - window.Top;
        rowsTotal += h;

        var clockFull = System.Diagnostics.Stopwatch.StartNew();
        var full = ReadRowsRapid(rapidOcr!, next, w, h, 1, false, "tight");
        fullMs += clockFull.Elapsed.TotalMilliseconds;

        var clockWin = System.Diagnostics.Stopwatch.StartNew();
        var strip = ReadRowsRapid(rapidOcr!, Crop(next, w, h, window.Top), w, h - window.Top, 1, false, "tight");
        windowMs += clockWin.Elapsed.TotalMilliseconds;

        Console.WriteLine($"  {Path.GetFileName(files[i]),-46} shift {window.Shift,4}px  read {window.Top,4}..{h}" +
                          $"  {(window.Whole ? "WHOLE" : "     ")}  full {full.Count,3} row(s) / window {strip.Count,3}");
    }
    Console.WriteLine();
    Console.WriteLine($"=== simulated {passes} consecutive pass(es), scrolling 3 rows each");
    Console.WriteLine($"  whole-frame reads      {whole} ({100.0 * whole / passes:F0}%)");
    Console.WriteLine($"  pixels read            {100.0 * rowsRead / rowsTotal:F0}% of the region");
    Console.WriteLine($"  full frame             {fullMs / passes:F0} ms/pass");
    Console.WriteLine($"  window only            {windowMs / passes:F0} ms/pass");
    return 0;
}

var totals = new Dictionary<string, Score>();
var unknownNames = new Dictionary<string, Dictionary<string, int>>();
var runs = (from e in engines from v in table select (Engine: e, Variant: v, Key: e + "/" + v.Name)).ToList();
foreach (var r in runs)
{
    totals[r.Key] = default;
    unknownNames[r.Key] = new Dictionary<string, int>(StringComparer.Ordinal);
}

var frame = 0;
foreach (var rawPath in rawFiles)
{
    var raw = LoadBgra(rawPath, out var w, out var h);
    var name = Path.GetFileName(rawPath);
    if (printRows) Console.WriteLine($"=== {name[..name.LastIndexOf('-')]}");

    foreach (var (engine, v, key) in runs)
    {
        var prepped = v.Keying is { } k ? k.Apply(raw, w, h) : raw;
        if (v.Invert) Invert(prepped);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var rows = engine == "rapid"
            ? ReadRowsRapid(rapidOcr!, prepped, w, h, v.Scale, v.Smooth, rapidPreset)
            : await ReadRowsAsync(winOcr, prepped, w, h, v.Scale, v.Smooth);
        var elapsed = clock.Elapsed.TotalMilliseconds;

        var score = totals[key];
        score.Rows += rows.Count;
        score.Millis += elapsed;
        foreach (var raw2 in rows)
        {
            var row = tolerant ? StripTag(raw2) : raw2;
            var parsed = LootParser.Parse(row);
            if (parsed.Kind != LootParser.Kind.Unrecognized) score.Loot++;
            if (vocab is null) continue;
            if (BracketName(row) is { } bracketed)
            {
                score.Named++;
                if (vocab.Contains(ItemNameKey(bracketed))) score.Clean++;
                else
                {
                    var bag = unknownNames[key];
                    bag.TryGetValue(bracketed, out var n);
                    bag[bracketed] = n + 1;
                }
            }
            // The end-to-end number: a complete reading, a real name, a
            // count. Anything less is a row the site never registers.
            if (parsed.Kind == LootParser.Kind.Item && vocab.Contains(ItemNameKey(parsed.Name))) score.Good++;
        }
        totals[key] = score;

        if (!printRows) continue;
        Console.WriteLine($"  {key,-18} rows {rows.Count,3}");
        foreach (var row in rows) Console.WriteLine($"      | {row}");
    }
    if (printRows) Console.WriteLine();
    frame++;
}

Console.WriteLine($"=== totals across {frame} frame(s)");
Console.WriteLine($"  {"engine/variant",-16}{"rows",6}{"loot",6}{"named",6}{"clean",6}{"good",6}  clean%   ms/frame");
foreach (var r in runs)
{
    var t = totals[r.Key];
    var pct = t.Named == 0 ? 0 : 100.0 * t.Clean / t.Named;
    Console.WriteLine($"  {r.Key,-16}{t.Rows,6}{t.Loot,6}{t.Named,6}{t.Clean,6}{t.Good,6}  {pct,5:F1}%   {t.Millis / Math.Max(frame, 1),6:F0}");
}

if (vocab is not null)
{
    Console.WriteLine();
    Console.WriteLine("=== names outside the vocabulary, by variant (top 15)");
    foreach (var r in runs)
    {
        Console.WriteLine($"  --- {r.Key}");
        foreach (var (n, c) in unknownNames[r.Key].OrderByDescending(p => p.Value).Take(15))
            Console.WriteLine($"      {c,4}  {n}");
    }
}
return 0;

/// <summary>
/// bdo's `public.item_name_key`, spelled here so the eval scores a reading the
/// way the register will: case and punctuation out, then the confusable-glyph
/// fold. Eval-only — the app never normalises a name, it ships it raw.
/// </summary>
static string ItemNameKey(string name)
{
    var norm = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    const string from = "01245689i";
    const string to = "olzasbbgl";
    var sb = new StringBuilder(norm.Length);
    foreach (var c in norm)
    {
        var at = from.IndexOf(c);
        sb.Append(at >= 0 ? to[at] : c);
    }
    return sb.ToString();
}

/// <summary>Frame `a` scrolled up by `scroll` px, with `b`'s bottom rows as the new arrivals.</summary>
static byte[] ScrollUp(byte[] a, byte[] b, int w, int h, int scroll)
{
    var dst = new byte[a.Length];
    var stride = w * 4;
    for (var y = 0; y < h; y++)
    {
        var from = y + scroll;
        if (from < h) Array.Copy(a, from * stride, dst, y * stride, stride);
        else Array.Copy(b, (h - (from - h) - 1) * stride, dst, y * stride, stride);
    }
    return dst;
}

static byte[] Crop(byte[] src, int w, int h, int top)
{
    var stride = w * 4;
    var dst = new byte[(h - top) * stride];
    Array.Copy(src, top * stride, dst, 0, dst.Length);
    return dst;
}

/// Black-on-white for the scene-text model.
static void Invert(byte[] bgra)
{
    for (var i = 0; i < bgra.Length; i += 4)
    {
        bgra[i] = (byte)(255 - bgra[i]);
        bgra[i + 1] = (byte)(255 - bgra[i + 1]);
        bgra[i + 2] = (byte)(255 - bgra[i + 2]);
    }
}

/// <summary>
/// The candidate engine: PaddleOCR PP-OCRv5 latin, detector + CRNN recognizer,
/// through ONNX Runtime on the CPU. Its detector finds its own lines, so the
/// same row-merge runs over its boxes for a like-for-like comparison with
/// Windows.Media.Ocr.
/// </summary>
static List<string> ReadRowsRapid(RapidOcr ocr, byte[] bgra, int w, int h, int scale, bool smooth, string preset)
{
    var scaled = smooth ? UpscaleSmooth(bgra, w, h, scale) : Upscale(bgra, w, h, scale);
    var info = new SKImageInfo(w * scale, h * scale, SKColorType.Bgra8888, SKAlphaType.Opaque);
    using var bmp = new SKBitmap(info);
    System.Runtime.InteropServices.Marshal.Copy(scaled, 0, bmp.GetPixels(), scaled.Length);
    // The detector's own resize is most of the fixed cost, and the chat is
    // already upright and already the right way up — nothing here needs the
    // 50px scan border or the 180° classifier.
    var options = preset switch
    {
        "compat" => RapidOcrOptions.PythonCompat with { DoAngle = false },
        "tight" => RapidOcrOptions.Default with { DoAngle = false, Padding = 0 },
        "tight736" => RapidOcrOptions.Default with { DoAngle = false, Padding = 0, ImgResize = 736 },
        _ => RapidOcrOptions.Default with { DoAngle = false },
    };
    if (Environment.GetEnvironmentVariable("EVAL_BOXES_ONLY") == "1")
    {
        // Detector only: how much of the candidate engine's cost is finding
        // the lines, versus reading them. The app already knows where its
        // rows are, so a detector it could skip is a cost it need not pay.
        ocr.DetectBoxes(bmp, options);
        return [];
    }
    var result = ocr.Detect(bmp, options);

    var pieces = new List<OcrRows.Piece>();
    foreach (var block in result.TextBlocks)
    {
        var text = block.Text?.Trim();
        if (string.IsNullOrEmpty(text)) continue;
        float x = float.MaxValue, top = float.MaxValue, bottom = float.MinValue;
        foreach (var pt in block.BoxPoints)
        {
            x = Math.Min(x, pt.X);
            top = Math.Min(top, pt.Y);
            bottom = Math.Max(bottom, pt.Y);
        }
        pieces.Add(new OcrRows.Piece(x / scale, top / scale, (bottom - top) / scale, text));
    }
    return OcrRows.Merge(pieces).Select(r => r.Text).ToList();
}

/// The chat's own tag box ("System") sits left of the verb and keys through
/// at lower core thresholds, where it merges into the row and the parser —
/// which demands the verb at position 0 — throws the whole pickup away. For
/// the sweep, strip anything before the verb so preprocessing is judged on
/// the reading, not on that one parser rule.
static string StripTag(string row)
{
    var at = row.IndexOf("You have obtained", StringComparison.OrdinalIgnoreCase);
    return at > 0 ? row[at..] : row;
}

/// The item name as the row actually reads it: between the first '[' and
/// the last ']', so a nested `[ [Event] Seal]` keeps its inner bracket the
/// way the app ships it raw. Null when the row has no closed bracket pair.
static string? BracketName(string row)
{
    var open = row.IndexOf('[');
    var close = row.LastIndexOf(']');
    if (open < 0 || close <= open + 1) return null;
    var inner = row[(open + 1)..close].Trim();
    return inner.Length == 0 ? null : inner;
}

// One visual row can come back as several OCR lines (keying splits at the
// icon gap). Merge lines that share a vertical band, left to right — that
// is what the app does too.
static async Task<List<string>> ReadRowsAsync(OcrEngine ocr, byte[] bgra, int w, int h, int scale, bool smooth)
{
    var scaled = smooth ? UpscaleSmooth(bgra, w, h, scale) : Upscale(bgra, w, h, scale);
    using var bmp = SoftwareBitmap.CreateCopyFromBuffer(scaled.AsBuffer(), BitmapPixelFormat.Bgra8, w * scale, h * scale, BitmapAlphaMode.Ignore);
    var result = await ocr.RecognizeAsync(bmp);

    var pieces = new List<OcrRows.Piece>();
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
        pieces.Add(new OcrRows.Piece(x / scale, top / scale, (bottom - top) / scale, text));
    }
    return OcrRows.Merge(pieces).Select(r => r.Text).ToList();
}

/// <summary>Nearest-neighbour, exactly what the app does today.</summary>
static byte[] Upscale(byte[] bgra, int w, int h, int scale)
{
    var dw = w * scale;
    var dst = new byte[dw * h * scale * 4];
    for (var y = 0; y < h * scale; y++)
    {
        var srcRow = y / scale * w;
        var dstRow = y * dw;
        for (var x = 0; x < dw; x++)
        {
            var s = (srcRow + x / scale) * 4;
            var d = (dstRow + x) * 4;
            dst[d] = bgra[s];
            dst[d + 1] = bgra[s + 1];
            dst[d + 2] = bgra[s + 2];
            dst[d + 3] = 255;
        }
    }
    return dst;
}

/// <summary>
/// Bilinear. Nearest-neighbour turns a 12px glyph into blocky staircases;
/// the game's own antialiasing is signal, and a smooth enlargement keeps the
/// stroke edges the recognizer's classifier was trained on.
/// </summary>
static byte[] UpscaleSmooth(byte[] bgra, int w, int h, int scale)
{
    var dw = w * scale;
    var dh = h * scale;
    var dst = new byte[dw * dh * 4];
    for (var y = 0; y < dh; y++)
    {
        var sy = (y + 0.5) / scale - 0.5;
        var y0 = (int)Math.Floor(sy);
        var fy = sy - y0;
        var y1 = Math.Clamp(y0 + 1, 0, h - 1);
        y0 = Math.Clamp(y0, 0, h - 1);
        for (var x = 0; x < dw; x++)
        {
            var sx = (x + 0.5) / scale - 0.5;
            var x0 = (int)Math.Floor(sx);
            var fx = sx - x0;
            var x1 = Math.Clamp(x0 + 1, 0, w - 1);
            x0 = Math.Clamp(x0, 0, w - 1);
            var d = (y * dw + x) * 4;
            for (var c = 0; c < 3; c++)
            {
                var a = bgra[(y0 * w + x0) * 4 + c] * (1 - fx) + bgra[(y0 * w + x1) * 4 + c] * fx;
                var b = bgra[(y1 * w + x0) * 4 + c] * (1 - fx) + bgra[(y1 * w + x1) * 4 + c] * fx;
                dst[d + c] = (byte)Math.Clamp(a * (1 - fy) + b * fy, 0, 255);
            }
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

record struct Score(int Rows, int Loot, int Named, int Clean, int Good, double Millis);

/// <summary>
/// One engine's tally over the balance corpus. `Shaped` is what got past the
/// strict shape and `Exact` is what was actually right — the gap between them
/// is the number that matters, because a shape-valid wrong figure has nothing
/// downstream to catch it.
/// </summary>
record struct BalanceScore(int Crops, int Shaped, int Refused, int Exact, int Wrong, double Millis);

/// <summary>
/// One preprocessing recipe. <paramref name="Invert"/> flips the keyed image
/// to black-on-white: the keyed frame is white text on black, and a scene-text
/// model trained on photographs of signage has seen far more of the opposite.
/// </summary>
record Variant(string Name, Key? Keying, int Scale, bool Smooth, bool Invert = false);

/// <summary>
/// The keying rule with its thresholds exposed, so the sweep can move one at
/// a time. <paramref name="Flatten"/> writes every text pixel at full white
/// instead of its own brightness: colored item names (green, orange) peak
/// dimmer than white chat text under max(R,G,B), and a recognizer that
/// thresholds internally may be reading them as faint.
/// </summary>
/// <summary>
/// What a text pixel is written as. <see cref="Out.Brightness"/> is what the
/// app ships. <see cref="Out.Flat"/> writes full white — colored item names
/// (green, orange) peak dimmer than white chat text under max(R,G,B), and a
/// recognizer that thresholds internally reads their strokes as thinner than
/// they are. <see cref="Out.Normalized"/> is the same correction without
/// throwing the antialiasing away: divide each stroke by the brightest text
/// near it, so a green name and a white verb arrive at the same contrast
/// while both keep their edge ramps.
/// </summary>
enum Out { Brightness, Flat, Normalized }

record Key(byte MinCore, byte MaxOutline, int Reach, Out Output)
{
    /// <summary>Radius of the "brightest text near it" window, in capture pixels — about one glyph.</summary>
    private const int NormReach = 6;

    public byte[] Apply(byte[] src, int width, int height)
    {
        var pixels = width * height;
        var bright = new byte[pixels];
        for (var i = 0; i < pixels; i++)
            bright[i] = Math.Max(src[i * 4], Math.Max(src[i * 4 + 1], src[i * 4 + 2]));

        var localMin = Window(bright, width, height, Reach, max: false);

        var isText = new bool[pixels];
        for (var i = 0; i < pixels; i++)
            isText[i] = bright[i] >= MinCore && localMin[i] <= MaxOutline;

        byte[]? localMax = null;
        if (Output == Out.Normalized)
        {
            var textOnly = new byte[pixels];
            for (var i = 0; i < pixels; i++) textOnly[i] = isText[i] ? bright[i] : (byte)0;
            localMax = Window(textOnly, width, height, NormReach, max: true);
        }

        var dst = new byte[src.Length];
        for (var i = 0; i < pixels; i++)
        {
            byte v = 0;
            if (isText[i])
                v = Output switch
                {
                    Out.Flat => 255,
                    Out.Normalized => (byte)Math.Min(255, bright[i] * 255 / Math.Max((int)localMax![i], 1)),
                    _ => bright[i],
                };
            dst[i * 4] = v;
            dst[i * 4 + 1] = v;
            dst[i * 4 + 2] = v;
            dst[i * 4 + 3] = 255;
        }
        return dst;
    }

    /// <summary>Separable min or max over a (2·reach+1)² window.</summary>
    private static byte[] Window(byte[] a, int width, int height, int reach, bool max)
    {
        var rows = new byte[a.Length];
        var outp = new byte[a.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var v = a[row + x];
                for (var d = 1; d <= reach; d++)
                {
                    if (x - d >= 0) v = Pick(v, a[row + x - d], max);
                    if (x + d < width) v = Pick(v, a[row + x + d], max);
                }
                rows[row + x] = v;
            }
        }
        for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
            {
                var v = rows[y * width + x];
                for (var d = 1; d <= reach; d++)
                {
                    if (y - d >= 0) v = Pick(v, rows[(y - d) * width + x], max);
                    if (y + d < height) v = Pick(v, rows[(y + d) * width + x], max);
                }
                outp[y * width + x] = v;
            }
        return outp;
    }

    private static byte Pick(byte a, byte b, bool max) => max ? Math.Max(a, b) : Math.Min(a, b);
}
