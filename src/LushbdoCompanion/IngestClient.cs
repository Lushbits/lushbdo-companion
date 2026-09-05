using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>
/// The two conversations this app has with the site: a batch of loot lines to
/// /gather/ingest, and a silver balance to /silver/record. The contract is the
/// server's; nothing here interprets what it carries.
///
/// One credential opens both — the site's own ruling (bdo#668), so a member who
/// has already paired posts balances without minting or pasting anything.
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

    /// <summary>
    /// The run a batch landed on. `elapsedSec` is gathering time — since
    /// Start, less every break — the figure the site's own clock shows.
    /// `liveSinceSec` is how long ago the session last went live: Start, or
    /// the latest Resume. It is what the pool cuts at, and a site that does
    /// not send it yet gets `elapsedSec` as the stand-in (Lushbits/bdo issue
    /// filed 2026-09-05).
    /// </summary>
    public sealed record SessionInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("elapsedSec")] long ElapsedSec,
        [property: JsonPropertyName("items")] int Items,
        [property: JsonPropertyName("liveSinceSec")] long? LiveSinceSec = null);

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

    /// <summary>
    /// The whole balance, which is the only thing this route takes. The site's
    /// column means the member's entire liquid silver and is read as that by
    /// the sheet total, the goal bar and bdo#663's series, so a partial figure
    /// posted here would make one column mean two things. A device that cannot
    /// establish it reads the whole figure must not post at all — that ruling
    /// is the route's, and #22's owner answered it for this app in the field.
    /// </summary>
    public sealed record SilverRecord([property: JsonPropertyName("silver")] long Silver);

    /// <summary>
    /// `stored:false` with `reason:"unchanged"` is a success, not a refusal —
    /// the figure already stood and the site deliberately wrote nothing. That
    /// is also what makes the route idempotent without an id: a redelivered
    /// *level* is the same claim rather than a second one.
    /// </summary>
    public sealed record SilverAnswer(
        [property: JsonPropertyName("silver")] long Silver,
        [property: JsonPropertyName("stored")] bool Stored,
        [property: JsonPropertyName("reason")] string? Reason);

    public sealed record SilverResult(
        bool Ok, int Status, string? Error, SilverAnswer? Answer, TimeSpan? RetryAfter);

    private sealed record Fault([property: JsonPropertyName("error")] string? Error);

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
    /// Record the member's whole liquid silver. Unlike a loot batch this is a
    /// *level*: redelivering it asserts the same thing rather than a second
    /// thing, so there is no batch id and no idempotency ring.
    /// </summary>
    public async Task<SilverResult> RecordSilverAsync(long silver, CancellationToken ct = default)
    {
        var token = settings.Token;
        if (token.Length == 0)
            return new SilverResult(false, 0, "No token — open Settings and paste one from the site's Devices page.", null, null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/silver/record");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new SilverRecord(silver));

            using var response = await Http.SendAsync(request, ct);
            var status = (int)response.StatusCode;

            if (status == 401) return new SilverResult(false, status, "The site does not recognise this token — it may have been revoked. Pair again from Settings → Devices.", null, null);
            if (status == 403) return new SilverResult(false, status, "This token is not a device token.", null, null);

            if (status == 503)
            {
                // The deploy-ahead-of-migration window the route documents. It
                // says how long to wait; honour it rather than guessing.
                var after = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                return new SilverResult(false, status,
                    await FaultAsync(response, ct) ?? "The site is still migrating.", null,
                    after is { TotalSeconds: > 0 } wait ? wait : TimeSpan.FromSeconds(60));
            }

            if (!response.IsSuccessStatusCode)
                return new SilverResult(false, status,
                    await FaultAsync(response, ct) ?? $"The site answered HTTP {status}.", null, null);

            var answer = await response.Content.ReadFromJsonAsync<SilverAnswer>(cancellationToken: ct);
            return answer is null
                ? new SilverResult(false, status, "The site answered something this version cannot read.", null, null)
                : new SilverResult(true, status, null, answer, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SilverResult(false, 0, "The site did not answer within 30 seconds.", null, null);
        }
        catch (HttpRequestException e)
        {
            return new SilverResult(false, 0, $"Could not reach the site: {e.Message}", null, null);
        }
    }

    /// <summary>The site names the rule a payload broke; carry its words rather than inventing any.</summary>
    private static async Task<string?> FaultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var fault = await response.Content.ReadFromJsonAsync<Fault>(cancellationToken: ct);
            return string.IsNullOrWhiteSpace(fault?.Error) ? null : fault.Error;
        }
        catch
        {
            return null; // not JSON, or not a shape this version knows
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
