using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>
/// Settings live in %APPDATA%\lushbdo-companion\settings.json. The token is
/// DPAPI-protected per user: a copied settings file on another machine (or
/// another account) decrypts to nothing, which is the point — the token is a
/// credential, not a preference.
/// </summary>
public sealed class Settings
{
    public string BaseUrl { get; set; } = "https://lushbdo.com";
    public string TokenProtected { get; set; } = "";

    /// <summary>Opt-in OCR diagnostics: raw per-pass lines and board decisions, written next to this file.</summary>
    public bool TraceOcr { get; set; }

    /// <summary>
    /// Read with the OS recognizer instead of PaddleOCR. PaddleOCR reads
    /// nearly twice as many field rows (#18) and is the default; this is the
    /// way back for a machine where it costs too much CPU, and the fallback if
    /// its models ever fail to unpack.
    /// </summary>
    public bool UseWindowsOcr { get; set; }

    /// <summary>
    /// The rectangles the app watches, by name. All of them are in physical
    /// pixels relative to the game window's visible top-left — the surface
    /// window capture serves — because window-relative coordinates survive the
    /// game restarting or the window moving.
    ///
    /// Only the loot log is required. The two balance rectangles (#22) are
    /// independently optional: somebody who never opens the marketplace never
    /// picks that one, and the app never asks for it.
    /// </summary>
    public enum RegionKind { Loot, Warehouse, Marketplace }

    /// <summary>One saved rectangle, window-relative physical pixels.</summary>
    public sealed class StoredRegion
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // The loot-chat rectangle keeps the four flat keys it has always had, so
    // an existing settings.json comes out the other side with its region
    // intact and nobody who has already picked is asked to pick again. The
    // rectangles that arrived with #22 are nested and nullable — absent means
    // never picked, which is a different thing from zero-sized.
    public int WindowRegionX { get; set; }
    public int WindowRegionY { get; set; }
    public int WindowRegionWidth { get; set; }
    public int WindowRegionHeight { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredRegion? WarehouseRegion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredRegion? MarketplaceRegion { get; set; }

    // Builds before window capture stored a screen-relative region under these
    // names. It cannot be translated without the game window it was picked
    // over, so migrating is one re-pick; the old values are only read so the
    // app can say why that re-pick is needed, and are scrubbed by it.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionY { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionWidth { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int RegionHeight { get; set; }

    [JsonIgnore]
    public bool HasScreenRelativeRegion => RegionWidth > 0 && RegionHeight > 0;

    public Rectangle? RegionFor(RegionKind kind) => kind switch
    {
        RegionKind.Loot => WindowRegionWidth > 0 && WindowRegionHeight > 0
            ? new Rectangle(WindowRegionX, WindowRegionY, WindowRegionWidth, WindowRegionHeight)
            : null,
        RegionKind.Warehouse => ToRectangle(WarehouseRegion),
        _ => ToRectangle(MarketplaceRegion),
    };

    public void SetRegion(RegionKind kind, Rectangle regionInWindow)
    {
        var stored = new StoredRegion
        {
            X = regionInWindow.X,
            Y = regionInWindow.Y,
            Width = regionInWindow.Width,
            Height = regionInWindow.Height,
        };
        switch (kind)
        {
            case RegionKind.Loot:
                WindowRegionX = stored.X;
                WindowRegionY = stored.Y;
                WindowRegionWidth = stored.Width;
                WindowRegionHeight = stored.Height;
                RegionX = RegionY = RegionWidth = RegionHeight = 0; // the migration ends here
                break;
            case RegionKind.Warehouse:
                WarehouseRegion = stored;
                break;
            default:
                MarketplaceRegion = stored;
                break;
        }
    }

    /// <summary>Drop both balance rectangles — the way out of a badly aimed one.</summary>
    public void ForgetBalanceRegions() => WarehouseRegion = MarketplaceRegion = null;

    /// <summary>The balance rectangles that have actually been picked, in menu order.</summary>
    [JsonIgnore]
    public IReadOnlyList<(RegionKind Kind, Rectangle Rect)> BalanceRegions
    {
        get
        {
            var picked = new List<(RegionKind, Rectangle)>(2);
            foreach (var kind in new[] { RegionKind.Warehouse, RegionKind.Marketplace })
                if (RegionFor(kind) is { } rect) picked.Add((kind, rect));
            return picked;
        }
    }

    private static Rectangle? ToRectangle(StoredRegion? stored) =>
        stored is { Width: > 0, Height: > 0 } ? new Rectangle(stored.X, stored.Y, stored.Width, stored.Height) : null;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lushbdo-companion");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                // Rewriting now is the scrub: nothing else here saves unless the
                // member opens Settings or switches recognizer, and a plaintext
                // credential must not wait on that.
                if (HasPlaintextToken(json)) settings.Save();
                return settings;
            }
        }
        catch
        {
            // A mangled file is a fresh start, not a crash at the tray.
        }
        return new Settings();
    }

    /// <summary>
    /// Builds through 0.5.0 serialized <see cref="Token"/> next to its own
    /// protected form, so the credential also sat in the file in plaintext.
    /// The property is ignored now, but the key is still on disk on every
    /// install that ever paired, and finding it is what forces the rewrite
    /// that drops it. The plaintext is not read back: <see cref="TokenProtected"/>
    /// carries the pairing forward on the machine that made it, and on any
    /// other machine the copied file is meant to be worth nothing.
    /// </summary>
    private static bool HasPlaintextToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Token", out _);
        }
        catch
        {
            return false;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Never serialized: persisting this would write the credential in
    /// plaintext beside the protected copy it decrypts from.
    /// </summary>
    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenProtected)) return "";
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return "";
            }
        }
        set
        {
            TokenProtected = value.Length == 0
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    [JsonIgnore]
    public bool IsPaired => TokenProtected.Length > 0 && Token.Length > 0;
}
