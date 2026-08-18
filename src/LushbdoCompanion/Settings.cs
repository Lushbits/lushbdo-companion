using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
