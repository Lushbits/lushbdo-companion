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
    /// Watch the silver rectangle and nothing else. The loot log is what costs
    /// CPU — it keys every captured frame and reads the chat on a good fraction
    /// of ticks — and a member who only wants their silver on the site should
    /// not pay for it. Field report: 16% on a laptop with the loot log on.
    ///
    /// This reverses the all-or-nothing ruling of 2026-08-30 morning, which was
    /// made when a silver-only mode had a cost and no benefit. The benefit
    /// arrived the same afternoon. It stays a mode rather than "drop the loot
    /// rectangle" so that switching back does not mean picking it again.
    /// </summary>
    public bool SilverOnly { get; set; }

    /// <summary>
    /// The rectangles the app watches, by name. All of them are in physical
    /// pixels relative to the game window's visible top-left — the surface
    /// window capture serves — because window-relative coordinates survive the
    /// game restarting or the window moving.
    ///
    /// Only the loot log is required. The marketplace rectangle (#22) is
    /// optional: somebody who never wants their silver read never picks it,
    /// and the app never asks for it.
    /// </summary>
    public enum RegionKind { Loot, Marketplace }

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
    public StoredRegion? MarketplaceRegion { get; set; }

    // Builds between #26 and the marketplace-only ruling had a second balance
    // rectangle for the warehouse panel. It is read only to carry a member's
    // aim forward into the one rectangle that remains, and is scrubbed by that
    // — the same treatment the screen-relative region above gets.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredRegion? WarehouseRegion { get; set; }

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

    /// <summary>
    /// This install had chosen the OS recognizer, which no longer exists. Set
    /// on load and never persisted, so the tray can say what became of that
    /// choice instead of silently moving the member onto the heavier reader —
    /// which is the one thing they had opted out of, and the reason the
    /// silver-only mode is worth pointing them at.
    /// </summary>
    [JsonIgnore]
    public bool HadWindowsOcrPreference { get; private set; }

    public Rectangle? RegionFor(RegionKind kind) => kind switch
    {
        RegionKind.Loot => WindowRegionWidth > 0 && WindowRegionHeight > 0
            ? new Rectangle(WindowRegionX, WindowRegionY, WindowRegionWidth, WindowRegionHeight)
            : null,
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
            default:
                MarketplaceRegion = stored;
                break;
        }
    }

    /// <summary>Drop one rectangle — the way out of a badly aimed one.</summary>
    public void ForgetRegion(RegionKind kind)
    {
        switch (kind)
        {
            case RegionKind.Loot:
                WindowRegionX = WindowRegionY = WindowRegionWidth = WindowRegionHeight = 0;
                break;
            default:
                MarketplaceRegion = null;
                break;
        }
    }

    /// <summary>The silver rectangle, if it has been picked.</summary>
    [JsonIgnore]
    public Rectangle? BalanceRegion => RegionFor(RegionKind.Marketplace);

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
                var scrub = HasPlaintextToken(json);
                if (ChoseWindowsOcr(json))
                {
                    settings.HadWindowsOcrPreference = true;
                    scrub = true;
                }
                // A warehouse rectangle from an older build becomes the one
                // remaining rectangle rather than being thrown away — the
                // member aimed it at their balance, and re-aiming it at the
                // market panel is a smaller ask than picking from nothing.
                if (settings.WarehouseRegion is { } legacy)
                {
                    settings.MarketplaceRegion ??= legacy;
                    settings.WarehouseRegion = null;
                    scrub = true;
                }
                if (scrub) settings.Save();
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
    /// Did this install ask for the OS recognizer? The setting is gone, so the
    /// key is read once to explain its disappearance and then scrubbed. Only
    /// `true` counts: somebody who left it off chose nothing and needs telling
    /// nothing.
    /// </summary>
    private static bool ChoseWindowsOcr(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("UseWindowsOcr", out var chosen)
                && chosen.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
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
