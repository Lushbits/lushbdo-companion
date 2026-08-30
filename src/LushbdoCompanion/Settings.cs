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

    // The loot-chat rectangle from the region picker, in physical pixels
    // relative to the game window's visible top-left — the surface window
    // capture serves. Window-relative on purpose: it survives the game
    // restarting or the window moving. Zero size means never picked.
    public int WindowRegionX { get; set; }
    public int WindowRegionY { get; set; }
    public int WindowRegionWidth { get; set; }
    public int WindowRegionHeight { get; set; }

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

    [JsonIgnore]
    public Rectangle? Region =>
        WindowRegionWidth > 0 && WindowRegionHeight > 0
            ? new Rectangle(WindowRegionX, WindowRegionY, WindowRegionWidth, WindowRegionHeight)
            : null;

    public void SetRegion(Rectangle regionInWindow)
    {
        WindowRegionX = regionInWindow.X;
        WindowRegionY = regionInWindow.Y;
        WindowRegionWidth = regionInWindow.Width;
        WindowRegionHeight = regionInWindow.Height;
        RegionX = RegionY = RegionWidth = RegionHeight = 0; // the migration ends here
    }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lushbdo-companion");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // A mangled file is a fresh start, not a crash at the tray.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

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

    public bool IsPaired => TokenProtected.Length > 0 && Token.Length > 0;
}
