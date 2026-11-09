using System.Text;
using System.Text.Json;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>Read-only AniList catalogue client. AniList IDs are external references only.</summary>
public class AniListApiService(HttpClient httpClient, ILogger<AniListApiService> logger)
{
    private const string ApiUrl = "https://graphql.anilist.co";
    // AniList's public API limit can be reduced without notice. Leave headroom below
    // 30 requests/minute and coordinate every typed-client instance in this process.
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(2250);
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(60);
    private const int MaxRequestAttempts = 5;
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset nextRequestAt = DateTimeOffset.MinValue;
    private const string MediaFields = @"
        id idMal title { romaji english native } synonyms
        coverImage { large } bannerImage description(asHtml: false) format episodes duration status
        averageScore startDate { year month day } endDate { year month day }
        genres tags { name category isMediaSpoiler }
        relations { edges { relationType node { id idMal title { romaji english native } } } }";

    public async Task<List<Anime>> GetSeasonAnimesAsync(int year, string season, Func<int, int, Task>? onPageFetched = null, CancellationToken ct = default)
    {
        var result = new List<Anime>();
        for (var page = 1; ; page++)
        {
            var data = await QueryAsync(@"query ($page: Int!, $year: Int!, $season: MediaSeason!) {
                Page(page: $page, perPage: 50) { pageInfo { hasNextPage } media(type: ANIME, seasonYear: $year, season: $season, sort: [POPULARITY_DESC]) { " + MediaFields + " } } }",
                new { page, year, season = season.ToUpperInvariant() }, ct);
            var pageData = data.GetProperty("Page");
            var animes = pageData.GetProperty("media").EnumerateArray().Select(MapFromAniList).ToList();
            result.AddRange(animes);
            if (onPageFetched != null) await onPageFetched(page, animes.Count);
            if (!pageData.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()) break;
        }
        return result;
    }

    public async Task<List<Anime>> GetCurrentAsync(bool upcoming, Func<int, int, Task>? onPageFetched = null, CancellationToken ct = default, int? maximumItems = null)
    {
        var status = upcoming ? "NOT_YET_RELEASED" : "RELEASING";
        var result = new List<Anime>();
        for (var page = 1; ; page++)
        {
            var data = await QueryAsync(@"query ($page: Int!, $status: MediaStatus!) {
                Page(page: $page, perPage: 50) { pageInfo { hasNextPage } media(type: ANIME, status: $status, sort: [POPULARITY_DESC]) { " + MediaFields + " } } }",
                new { page, status }, ct);
            var pageData = data.GetProperty("Page");
            var animes = pageData.GetProperty("media").EnumerateArray().Select(MapFromAniList).ToList();
            result.AddRange(animes);
            if (onPageFetched != null) await onPageFetched(page, animes.Count);
            if (maximumItems is int maximum && result.Count >= maximum)
                return result.Take(maximum).ToList();
            if (!pageData.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()) break;
        }
        return result;
    }

    /// <summary>Returns AniList's ranking for a catalogue slice without substituting a local sort.</summary>
    public async Task<List<Anime>> GetTopAnimeAsync(string? format = null, string? status = null, string sort = "POPULARITY_DESC", CancellationToken ct = default, int maximumItems = 1_000)
    {
        var result = new List<Anime>();
        for (var page = 1; result.Count < maximumItems; page++)
        {
            var data = await QueryAsync(@"query ($page: Int!, $format: MediaFormat, $status: MediaStatus, $sort: [MediaSort!]) {
                Page(page: $page, perPage: 50) { pageInfo { hasNextPage } media(type: ANIME, format: $format, status: $status, sort: $sort) { " + MediaFields + " } } }",
                new { page, format, status, sort = new[] { sort } }, ct);
            var pageData = data.GetProperty("Page");
            result.AddRange(pageData.GetProperty("media").EnumerateArray().Select(MapFromAniList));
            if (!pageData.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean()) break;
        }
        return result.Take(maximumItems).ToList();
    }

    public async Task<List<Anime>> SearchAsync(string search, CancellationToken ct = default)
    {
        var data = await QueryAsync("query ($search: String!) { Page(perPage: 25) { media(type: ANIME, search: $search) { " + MediaFields + " } } }", new { search }, ct);
        return data.GetProperty("Page").GetProperty("media").EnumerateArray().Select(MapFromAniList).ToList();
    }

    public async Task<Anime?> GetByAniListIdAsync(int aniListId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query ($id: Int!) { Media(id: $id, type: ANIME) { " + MediaFields + " } }", new { id = aniListId }, ct);
        var media = data.GetProperty("Media");
        return media.ValueKind == JsonValueKind.Null ? null : MapFromAniList(media);
    }

    public async Task<Anime?> GetByMalIdAsync(int malId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query ($id: Int!) { Media(idMal: $id, type: ANIME) { " + MediaFields + " } }", new { id = malId }, ct);
        var media = data.GetProperty("Media");
        return media.ValueKind == JsonValueKind.Null ? null : MapFromAniList(media);
    }

    /// <summary>
    /// Resolves both provider IDs for one AniList or MAL ID, or null when AniList
    /// has no such anime. Used to confirm catalogue identity without a title match.
    /// </summary>
    public async Task<ProviderIds?> GetProviderIdsAsync(int? aniListId, int? malId, CancellationToken ct = default)
    {
        var (query, variables) = aniListId is > 0
            ? ("query ($id: Int!) { Media(id: $id, type: ANIME) { id idMal } }", new { id = aniListId.Value })
            : malId is > 0
                ? ("query ($id: Int!) { Media(idMal: $id, type: ANIME) { id idMal } }", new { id = malId.Value })
                : throw new ArgumentException("An AniList or MAL ID is required.");
        try
        {
            var media = (await QueryAsync(query, variables, ct)).GetProperty("Media");
            return media.ValueKind == JsonValueKind.Null ? null : new ProviderIds(media.GetProperty("id").GetInt32(), GetInt(media, "idMal"));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<(Anime Anime, string RelationType)>> GetRelationsAsync(int aniListId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query ($id: Int!) { Media(id: $id, type: ANIME) { relations { edges { relationType node { " + MediaFields + " } } } } }", new { id = aniListId }, ct);
        return data.GetProperty("Media").GetProperty("relations").GetProperty("edges").EnumerateArray()
            .Select(x => (MapFromAniList(x.GetProperty("node")), GetString(x, "relationType") ?? "Related"))
            .ToList();
    }

    public async Task<List<(Anime Anime, int Rating)>> GetRecommendationsAsync(int aniListId, CancellationToken ct = default)
    {
        var data = await QueryAsync("query ($id: Int!) { Media(id: $id, type: ANIME) { recommendations { edges { node { rating mediaRecommendation { " + MediaFields + " } } } } } }", new { id = aniListId }, ct);
        return data.GetProperty("Media").GetProperty("recommendations").GetProperty("edges").EnumerateArray()
            .Where(edge => edge.GetProperty("node").GetProperty("mediaRecommendation").ValueKind != JsonValueKind.Null)
            .Select(edge =>
            {
                var node = edge.GetProperty("node");
                return (MapFromAniList(node.GetProperty("mediaRecommendation")), GetInt(node, "rating") ?? 0);
            })
            .ToList();
    }

    private async Task<JsonElement> QueryAsync(string query, object variables, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            await WaitForRequestSlotAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json")
            };
            using var response = await httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("errors", out var errors))
                    throw new HttpRequestException($"AniList GraphQL error: {errors}");
                return doc.RootElement.GetProperty("data").Clone();
            }

            var error = new HttpRequestException($"AniList returned {(int)response.StatusCode}: {body}", null, response.StatusCode);
            if (!IsTransient(response.StatusCode) || attempt == MaxRequestAttempts)
                throw error;

            var delay = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? RetryDelay(response)
                : TimeSpan.FromSeconds(Math.Pow(2, attempt));
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                await PauseRequestsAsync(delay, ct);

            lastError = error;
            logger.LogWarning(error, "AniList request failed (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                attempt, MaxRequestAttempts, delay);
            await Task.Delay(delay, ct);
        }

        throw new HttpRequestException($"AniList request failed after {MaxRequestAttempts} attempts.", lastError);
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode == System.Net.HttpStatusCode.TooManyRequests || (int)statusCode >= StatusCodes.Status500InternalServerError;

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero) return delta;
        if (retryAfter?.Date is DateTimeOffset date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) return remaining;
        }
        return DefaultRateLimitDelay;
    }

    private static async Task WaitForRequestSlotAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var delay = nextRequestAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;
        }
        finally { RequestGate.Release(); }
    }

    private static async Task PauseRequestsAsync(TimeSpan delay, CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var retryAt = DateTimeOffset.UtcNow + delay;
            if (retryAt > nextRequestAt) nextRequestAt = retryAt;
        }
        finally { RequestGate.Release(); }
    }

    internal static Anime MapFromAniList(JsonElement element)
    {
        var titles = element.GetProperty("title");
        var english = GetString(titles, "english");
        var romaji = GetString(titles, "romaji");
        var native = GetString(titles, "native");
        var anime = new Anime
        {
            AniListId = element.GetProperty("id").GetInt32(),
            MALId = GetInt(element, "idMal"),
            Title = english ?? romaji ?? native ?? "Unknown",
            HasEnglishTitle = !string.IsNullOrWhiteSpace(english),
            DataSource = "AniList", SourceEnglishTitle = english,
            OriginalTitle = romaji ?? native,
            AlternativeTitles = new[] { english, romaji, native }
                .Concat(element.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array
                    ? synonyms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())
                    : [])
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList(),
            Synopsis = GetString(element, "description")?.Replace("<br>", "\n").Replace("<i>", "").Replace("</i>", "").Replace("<b>", "").Replace("</b>", "").Trim(),
            ImageUrl = element.TryGetProperty("coverImage", out var image) ? GetString(image, "large") : null,
            SourceImages = Artwork(element, image),
            Type = GetString(element, "format")?.Replace('_', ' '), Episodes = GetInt(element, "episodes"), Duration = GetInt(element, "duration"),
            Rating = GetInt(element, "averageScore") is int score ? score / 10m : null,
            Status = NormalizeStatus(GetString(element, "status")), StartDate = ParseDate(element, "startDate"), EndDate = ParseDate(element, "endDate")
        };
        if (element.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Object &&
            relations.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            foreach (var edge in edges.EnumerateArray())
                if (edge.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object)
                    anime.ProviderRelations.Add(new() { RelationType = GetString(edge, "relationType") ?? "RELATED", AniListId = GetInt(node, "id"), MalId = GetInt(node, "idMal") });
        if (element.TryGetProperty("genres", out var genres)) foreach (var genre in genres.EnumerateArray()) if (genre.GetString() is { Length: > 0 } name) anime.AnimeGenres.Add(new() { Genre = new() { Name = name } });
        if (element.TryGetProperty("tags", out var tags)) foreach (var tag in tags.EnumerateArray())
        {
            if (tag.TryGetProperty("isMediaSpoiler", out var spoiler) && spoiler.GetBoolean()) continue;
            var name = GetString(tag, "name"); if (string.IsNullOrWhiteSpace(name)) continue;
            if (string.Equals(GetString(tag, "category"), "Demographic", StringComparison.OrdinalIgnoreCase)) anime.AnimeDemographics.Add(new() { Demographic = new() { Name = name } });
            else anime.AnimeThemes.Add(new() { Theme = new() { Name = name } });
        }
        return anime;
    }

    private static string? GetString(JsonElement el, string name) => el.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
    private static List<SourceImage> Artwork(JsonElement element, JsonElement cover)
    {
        var images = new List<SourceImage>();
        if (GetString(cover, "large") is { Length: > 0 } poster) images.Add(new() { Source = "AniList", Type = "Poster", Url = poster });
        if (GetString(element, "bannerImage") is { Length: > 0 } banner) images.Add(new() { Source = "AniList", Type = "Banner", Url = banner });
        return images;
    }
    private static int? GetInt(JsonElement el, string name) => el.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;
    private static DateTime? ParseDate(JsonElement el, string name) { if (!el.TryGetProperty(name, out var d) || GetInt(d, "year") is not int y || GetInt(d, "month") is not int m || GetInt(d, "day") is not int day) return null; try { return new DateTime(y, m, day); } catch { return null; } }
    private static string? NormalizeStatus(string? status) => status switch { "RELEASING" => "Currently Airing", "FINISHED" => "Finished", "NOT_YET_RELEASED" => "Not Yet Aired", "CANCELLED" => "Cancelled", "HIATUS" => "Hiatus", _ => status?.Replace('_', ' ') };
}

/// <summary>The AniList and MAL identifiers AniList holds for one anime.</summary>
public sealed record ProviderIds(int AniListId, int? MalId);
