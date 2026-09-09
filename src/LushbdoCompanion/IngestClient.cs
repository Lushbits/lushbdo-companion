using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace LushbdoCompanion;

/// <summary>
/// The conversations this app has with the site: a batch of loot lines to
/// /gather/ingest, a silver balance to /silver/record, and — the one read —
/// an item icon from /icons for a slot the reply named (#52, bdo#728). The
/// contract is the server's; nothing here interprets what it carries.
///
/// One credential opens all of them — the site's own ruling (bdo#668), so a
/// member who has already paired posts balances and fetches icons without
/// minting or pasting anything.
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
    /// the latest Resume, and null while paused. It is what the pool cuts at
    /// (bdo#707, shipped in bdo#708); a site from before it gets `elapsedSec`
    /// as the stand-in.
    /// </summary>
    public sealed record SessionInfo(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("elapsedSec")] long ElapsedSec,
        [property: JsonPropertyName("items")] int Items,
        [property: JsonPropertyName("liveSinceSec")] long? LiveSinceSec = null,
        // What the run is worth so far and its pace, whole silver, valued on
        // the live market the way the session page values it (bdo#724,
        // shipped in bdo#725): gross, and net of the member's own market
        // tax. Null is "nothing on the sheet could be valued", never zero,
        // and `unvaluedRows` says how many rows that left out. Absent on a
        // site from before it; the overlay (#39) shows the count and a dash
        // then.
        [property: JsonPropertyName("valueGross")] long? ValueGross = null,
        [property: JsonPropertyName("valueNet")] long? ValueNet = null,
        [property: JsonPropertyName("silverPerHourGross")] long? SilverPerHourGross = null,
        [property: JsonPropertyName("silverPerHourNet")] long? SilverPerHourNet = null,
        [property: JsonPropertyName("unvaluedRows")] int UnvaluedRows = 0,
        // The three item slots the member set on the site (bdo#728), in slot
        // order, null where a slot is empty — so slot 2 stays slot 2 when
        // slot 1 is cleared. Absent on a site from before it, and the
        // overlay (#52) draws none then. Nothing here chooses an item: the
        // app draws whatever the site names, and only that.
        [property: JsonPropertyName("slots")] IReadOnlyList<SlotInfo?>? Slots = null);

    /// <summary>
    /// One filled slot: the item the member is watching, the session row's
    /// running count, and the path of its icon on the site — nullable, since
    /// about 1.8% of items have no art. The name is the site's own spelling.
    /// </summary>
    public sealed record SlotInfo(
        [property: JsonPropertyName("itemId")] long ItemId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("qty")] long Qty,
        [property: JsonPropertyName("iconPath")] string? IconPath = null);

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

    /// <summary>
    /// One icon, fetched the way the browser fetches it: `GET /icons/&lt;path&gt;`
    /// on the same credential, for a path a reply named (bdo#728 opens the
    /// route to a paired device for exactly that). `NotModified` is the site
    /// saying the cached bytes still stand — the day-long cache the route was
    /// built for, honoured here by carrying the ETag back — and `Bytes` is
    /// null then. `Status` 404 is an item whose art is genuinely absent, and
    /// 401 is what it is everywhere else: revoked.
    /// </summary>
    public sealed record IconResult(bool Ok, int Status, byte[]? Bytes, string? ETag, string? Error)
    {
        public bool NotModified => Ok && Status == 304;
    }

    public async Task<IconResult> FetchIconAsync(string path, string? ifNoneMatch, CancellationToken ct = default)
    {
        var token = settings.Token;
        if (token.Length == 0) return new IconResult(false, 0, null, null, "No token.");
        if (!IsIconPath(path)) return new IconResult(false, 0, null, null, "Not an icon path.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.BaseUrl.TrimEnd('/')}/icons/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(ifNoneMatch))
                request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

            using var response = await Http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var etag = response.Headers.ETag?.ToString();
            if (status == 304) return new IconResult(true, status, null, etag, null);
            if (status == 401) return new IconResult(false, status, null, null, "The site does not recognise this token.");
            if (status == 403) return new IconResult(false, status, null, null, "This token may not read icons.");
            if (!response.IsSuccessStatusCode)
                return new IconResult(false, status, null, null, $"The site answered HTTP {status}.");

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return new IconResult(true, status, bytes, etag, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IconResult(false, 0, null, null, "The site did not answer within 30 seconds.");
        }
        catch (HttpRequestException e)
        {
            return new IconResult(false, 0, null, null, $"Could not reach the site: {e.Message}");
        }
    }

    /// <summary>
    /// The site's own rule for what an icon key looks like (`isIconPath` in
    /// its `$lib/icons`): lowercase, from the game's archive tree, ending in
    /// `.webp`, with no empty, `.` or `..` segment. The app only ever asks for
    /// paths the site named on a reply, and checking them anyway is what makes
    /// that true by construction rather than by trust — nothing here can be
    /// talked into fetching anything but an icon.
    /// </summary>
    public static bool IsIconPath(string path)
    {
        if (path.Length is 0 or > 255) return false;
        if (!path.EndsWith(".webp", StringComparison.Ordinal)) return false;
        if (!char.IsAsciiLetterLower(path[0]) && !char.IsAsciiDigit(path[0])) return false;
        foreach (var c in path)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '/' or '-')) return false;
        foreach (var segment in path.Split('/'))
            if (segment is "" or "." or "..") return false;
        return true;
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
