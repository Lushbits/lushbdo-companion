using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>
/// The one conversation this app has with the site: POST a batch of loot lines
/// to /gather/ingest and read back what happened to each. The contract is the
/// server's; nothing here interprets a line beyond carrying it.
/// </summary>
public sealed class IngestClient(Settings settings)
{
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("lushbdo-companion", version));
        return client;
    }

    public sealed record Line(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("count")] int Count);

    public sealed record Batch(
        [property: JsonPropertyName("batchId")] string BatchId,
        [property: JsonPropertyName("lines")] IReadOnlyList<Line> Lines);

    public sealed record SessionInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("elapsedSec")] long ElapsedSec,
        [property: JsonPropertyName("items")] int Items);

    public sealed record MatchedLine(
        [property: JsonPropertyName("line")] string LineText,
        [property: JsonPropertyName("itemId")] long ItemId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("added")] int Added,
        [property: JsonPropertyName("qty")] int Qty);

    public sealed record HeldLine(
        [property: JsonPropertyName("line")] string LineText,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("why")] string Why);

    public sealed record IngestAnswer(
        [property: JsonPropertyName("applied")] bool Applied,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("session")] SessionInfo? Session,
        [property: JsonPropertyName("matched")] IReadOnlyList<MatchedLine>? Matched,
        [property: JsonPropertyName("held")] IReadOnlyList<HeldLine>? Held,
        [property: JsonPropertyName("dropped")] IReadOnlyList<HeldLine>? Dropped);

    public sealed record Result(bool Ok, int Status, string? Error, IngestAnswer? Answer);

    public async Task<Result> SendAsync(Batch batch, CancellationToken ct = default)
    {
        var token = settings.Token;
        if (token.Length == 0)
            return new Result(false, 0, "No token — open Settings and paste one from the site's Devices page.", null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/gather/ingest");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(batch);

            using var response = await Http.SendAsync(request, ct);
            var status = (int)response.StatusCode;

            if (status == 401) return new Result(false, status, "The site does not recognise this token — it may have been revoked. Pair again from Settings → Devices.", null);
            if (status == 403) return new Result(false, status, "This token is not a device token.", null);
            if (!response.IsSuccessStatusCode)
                return new Result(false, status, $"The site answered HTTP {status}.", null);

            var answer = await response.Content.ReadFromJsonAsync<IngestAnswer>(cancellationToken: ct);
            return answer is null
                ? new Result(false, status, "The site answered something this version cannot read.", null)
                : new Result(true, status, null, answer);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Result(false, 0, "The site did not answer within 30 seconds.", null);
        }
        catch (HttpRequestException e)
        {
            return new Result(false, 0, $"Could not reach the site: {e.Message}", null);
        }
    }

    /// <summary>
    /// The same shape the server-side feeder script sends: one clean reading,
    /// one deliberately mangled one, one that nothing can match. Exercises
    /// every verdict the answer can carry.
    /// </summary>
    public static Batch TestBatch() => new(
        $"companion-test-{Guid.NewGuid():N}",
        [
            new Line("Rough Stone", 2),
            new Line("R0ugh St0ne", 1),
            new Line("Companion Test Nonsuch", 1)
        ]);
}
