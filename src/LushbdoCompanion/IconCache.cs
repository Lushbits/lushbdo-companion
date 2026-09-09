using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace LushbdoCompanion;

/// <summary>
/// The item icons the slots draw (#52), fetched from the site once per path
/// and kept. An icon is a ~3 KB webp the site serves from a private bucket
/// behind the member's own credential (bdo#728), so it is asked for exactly
/// the way the browser asks: by the path a reply named, with the bearer
/// token, and never enumerated — nothing here can ask for an icon the site
/// did not first hand this device.
///
/// Kept under %LOCALAPPDATA% beside the OCR models, keyed by a hash of the
/// path, with the site's ETag beside it: the day-long cache the route was
/// built for is honoured by asking again no more than daily and carrying the
/// tag, so a revalidation costs a 304 and not the bytes. What is on disk is
/// only ever the icons of items that were on this member's own sessions,
/// which is not the item data the app must never ship — but it is extracted
/// artwork on a member's disk, so the settings page says it is there and
/// offers to clear it.
///
/// Decoded with the SkiaSharp that already rides in the exe for OCR, so no
/// native dependency is added — CLAUDE.md's `dumpbin` rule is met without a
/// second look. Nothing else in the app learns to decode webp.
///
/// The dictionary belongs to the UI thread: <see cref="Get"/> is asked from a
/// paint, a load runs on the pool and posts its answer back, and
/// <see cref="Loaded"/> is what tells the overlay to paint again. A fetch that
/// fails draws the count without its picture and is tried again a few minutes
/// later; it never holds a paint, and says so in the log once per run rather
/// than once per failure. A 401 is what it is everywhere else — revoked — and
/// the fetches stop for the run.
/// </summary>
public sealed class IconCache : IDisposable
{
    private static readonly TimeSpan Revalidate = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>A slot icon is 44 px in the game's own tree; anything the site would serve is far below this, so a decode above it is refused rather than allocated.</summary>
    private const int MaxSide = 1024;

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "lushbdo-companion", "icons");

    private readonly IngestClient _client;
    private readonly Action<string> _log;
    private readonly SynchronizationContext? _ui;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private bool _saidUnreachable;
    private bool _saidRefused;
    private bool _saidUndecodable;
    private bool _refused;   // the site rejected the token: no more asking this run
    private bool _disposed;

    private sealed class Entry
    {
        public Bitmap? Image;
        public bool Loading;
        public bool Gone;          // the site has no art for it, or could not be decoded: not asked again this run
        public DateTime NextTry;   // after a failure that may pass, when to ask again
    }

    private enum Kind { Ok, Gone, Refused, Undecodable, Unreachable }

    private readonly record struct Outcome(Kind Kind, Bitmap? Image, string? Error);

    /// <summary>An icon arrived and the slots want painting again. Raised on the thread that made the cache.</summary>
    public event Action? Loaded;

    public IconCache(IngestClient client, Action<string> log)
    {
        _client = client;
        _log = log;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>
    /// The icon for this path if it is in hand, else null — and a load
    /// begins, unless one already has or this path has been given up on.
    /// The bitmap is the cache's: draw it, do not dispose it.
    /// </summary>
    public Bitmap? Get(string path)
    {
        if (_disposed || !IngestClient.IsIconPath(path)) return null;
        if (!_entries.TryGetValue(path, out var entry)) _entries[path] = entry = new Entry();
        if (entry.Image is not null) return entry.Image;
        if (entry.Loading || entry.Gone || _refused || DateTime.UtcNow < entry.NextTry) return null;
        entry.Loading = true;
        _ = LoadAsync(path, entry);
        return null;
    }

    /// <summary>Drop everything, on disk and in hand. The next paint fetches again — one request per slot, once.</summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Image?.Dispose();
        _entries.Clear();
        ClearDisk();
    }

    /// <summary>What is on disk, as a count and a size, for the settings page to say.</summary>
    public static (int Files, long Bytes) OnDisk()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return (0, 0);
            var files = 0;
            long bytes = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.webp"))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }
            return (files, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static void ClearDisk()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // A file held open, or a folder already gone: the next fetch overwrites what is left.
        }
    }

    private async Task LoadAsync(string path, Entry entry)
    {
        Outcome outcome;
        try
        {
            outcome = await Task.Run(() => LoadOnceAsync(path, _stop.Token), _stop.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            outcome = new Outcome(Kind.Unreachable, null, e.Message);
        }

        Post(() =>
        {
            if (_disposed)
            {
                outcome.Image?.Dispose();
                return;
            }
            entry.Loading = false;
            switch (outcome.Kind)
            {
                case Kind.Ok:
                    entry.Image?.Dispose();
                    entry.Image = outcome.Image;
                    Loaded?.Invoke();
                    break;
                case Kind.Gone:
                    entry.Gone = true;
                    break;
                case Kind.Undecodable:
                    entry.Gone = true;
                    if (!_saidUndecodable)
                    {
                        _saidUndecodable = true;
                        _log("icons  the site sent an icon this version cannot decode — its slot shows the count without it.");
                    }
                    break;
                case Kind.Refused:
                    _refused = true;
                    if (!_saidRefused)
                    {
                        _saidRefused = true;
                        _log($"icons  {outcome.Error} Slot icons are off until this device is paired again.");
                    }
                    break;
                default:
                    entry.NextTry = DateTime.UtcNow + RetryAfter;
                    if (!_saidUnreachable)
                    {
                        _saidUnreachable = true;
                        _log($"icons  could not fetch an item icon ({outcome.Error}) — the count shows without it; trying again in a few minutes.");
                    }
                    break;
            }
        });
    }

    /// <summary>
    /// Disk first, then the site: fresh bytes on disk are the answer; stale
    /// ones are offered back to the site with their tag, and stand if it
    /// says they do — or if it cannot be reached at all, since a day-old
    /// icon is not wrong. Off the UI thread throughout.
    /// </summary>
    private async Task<Outcome> LoadOnceAsync(string path, CancellationToken ct)
    {
        var file = FileFor(path);
        var tagFile = file + ".etag";
        byte[]? bytes = null;
        var fresh = false;
        try
        {
            if (File.Exists(file))
            {
                bytes = await File.ReadAllBytesAsync(file, ct);
                fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < Revalidate;
            }
        }
        catch (IOException)
        {
            bytes = null;
        }
        if (bytes is not null && fresh) return Decoded(bytes);

        string? etag = null;
        if (bytes is not null && File.Exists(tagFile))
        {
            try { etag = await File.ReadAllTextAsync(tagFile, ct); }
            catch (IOException) { etag = null; }
        }

        var result = await _client.FetchIconAsync(path, etag, ct);
        if (result.NotModified && bytes is not null)
        {
            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
            catch (IOException) { /* it is revalidated again tomorrow, which costs a 304 */ }
            return Decoded(bytes);
        }
        if (result.Ok && result.Bytes is not null)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                await File.WriteAllBytesAsync(file, result.Bytes, ct);
                if (result.ETag is { Length: > 0 } tag) await File.WriteAllTextAsync(tagFile, tag, ct);
                else if (File.Exists(tagFile)) File.Delete(tagFile);
            }
            catch (IOException)
            {
                // Drawn from memory this run and fetched again next: a cache that cannot write is still a cache.
            }
            return Decoded(result.Bytes);
        }
        if (result.Status == 404)
        {
            // The site's own silent case — an item whose declared file is
            // absent from the extract. What was on disk is no longer vouched for.
            try
            {
                if (File.Exists(file)) File.Delete(file);
                if (File.Exists(tagFile)) File.Delete(tagFile);
            }
            catch (IOException) { /* stale bytes nobody will read again */ }
            return new Outcome(Kind.Gone, null, null);
        }
        if (result.Status is 401 or 403) return new Outcome(Kind.Refused, null, result.Error);
        // Unreachable, or an answer this version does not know: what is on
        // disk still stands, and nothing on disk means trying again later.
        return bytes is not null ? Decoded(bytes) : new Outcome(Kind.Unreachable, null, result.Error);
    }

    private static Outcome Decoded(byte[] bytes)
    {
        var image = Decode(bytes);
        return image is null ? new Outcome(Kind.Undecodable, null, null) : new Outcome(Kind.Ok, image, null);
    }

    /// <summary>
    /// webp to a GDI+ bitmap, through Skia. Premultiplied BGRA is what both
    /// sides speak natively — Skia's Bgra8888/Premul and GDI+'s 32bppPArgb
    /// are the same bytes — so the copy is row by row and nothing is
    /// converted. Null for anything that is not a picture this size.
    /// </summary>
    private static Bitmap? Decode(byte[] bytes)
    {
        SKImageInfo bounds;
        try
        {
            bounds = SKBitmap.DecodeBounds(bytes);
        }
        catch
        {
            return null;
        }
        if (bounds.Width is <= 0 or > MaxSide || bounds.Height is <= 0 or > MaxSide) return null;

        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var skia = SKBitmap.Decode(bytes, info);
        if (skia is null || skia.ColorType != SKColorType.Bgra8888) return null;

        var bitmap = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, info.Width, info.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            unsafe
            {
                var src = (byte*)skia.GetPixels();
                var dst = (byte*)data.Scan0;
                var rowBytes = info.Width * 4;
                for (var y = 0; y < info.Height; y++)
                    Buffer.MemoryCopy(src + y * skia.RowBytes, dst + y * data.Stride, rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>The file for a path: a hash, so a path's slashes and length never reach the file system.</summary>
    private static string FileFor(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Path.Combine(Directory, Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant() + ".webp");
    }

    private void Post(Action action)
    {
        if (_ui is null) action();
        else _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        foreach (var entry in _entries.Values) entry.Image?.Dispose();
        _entries.Clear();
        _stop.Dispose();
    }
}
