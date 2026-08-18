using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Net.Http.Json;

namespace LushbdoCompanion;

/// <summary>
/// Asks GitHub for the newest release and compares it to the running version.
/// Notice-and-link only — no self-updating machinery: the answer to "am I
/// stale" is a balloon tip whose click opens the download page.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Lushbits/lushbdo-companion/releases/latest";
    public const string ReleasesPage = "https://github.com/Lushbits/lushbdo-companion/releases/latest";

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);

    public sealed record Check(bool UpdateAvailable, string? LatestVersion, string? Error);

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<Check> RunAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("lushbdo-companion", Current.ToString(3)));

            using var response = await client.GetAsync(LatestReleaseApi, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Check(false, null, null); // No release published yet — nothing to be behind.
            if (!response.IsSuccessStatusCode)
                return new Check(false, null, $"GitHub answered HTTP {(int)response.StatusCode}.");

            var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: ct);
            var tag = release?.TagName?.TrimStart('v', 'V');
            if (tag is null || !Version.TryParse(tag, out var latest))
                return new Check(false, null, "The newest release's version could not be read.");

            return new Check(latest > Current, latest.ToString(3), null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return new Check(false, null, "Could not reach GitHub to check for updates.");
        }
    }
}
