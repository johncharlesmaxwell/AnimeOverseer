using System.Text.Json;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

public class JikanApiService : IAnimeDataSource
{
    private const int MaxRequestAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly ILogger<JikanApiService> _logger;

    public JikanApiService(HttpClient httpClient, ILogger<JikanApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<Anime>> GetSeasonAnimes(int year, string season, bool forceRefresh = false)
        => await FetchSeasonPagesAsync(year, season);

    internal Task<List<Anime>> FetchSeasonPagesAsync(
        int year, string season, Func<int, int, Task>? onPageFetched = null, CancellationToken ct = default)
        => FetchPagedAsync($"https://api.jikan.moe/v4/seasons/{year}/{season}", onPageFetched, ct);

    internal Task<List<Anime>> FetchSeasonNowPagesAsync(Func<int, int, Task>? onPageFetched = null)
        => FetchPagedAsync("https://api.jikan.moe/v4/seasons/now", onPageFetched);

    internal Task<List<Anime>> FetchSeasonUpcomingPagesAsync(Func<int, int, Task>? onPageFetched = null)
        => FetchPagedAsync("https://api.jikan.moe/v4/seasons/upcoming", onPageFetched);

    private async Task<List<Anime>> FetchPagedAsync(string baseUrl, Func<int, int, Task>? onPageFetched = null, CancellationToken ct = default)
    {
        var animes = new List<Anime>();
        var page = 1;
        try
        {
            while (true)
            {
                var sep = baseUrl.Contains('?') ? "&" : "?";
                using var response = await GetWithRetryAsync($"{baseUrl}{sep}page={page}", ct);

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                var pageAnimes = new List<Anime>();
                foreach (var anime in doc.RootElement.GetProperty("data").EnumerateArray())
                    pageAnimes.Add(MapFromJikan(anime));
                animes.AddRange(pageAnimes);

                if (onPageFetched != null)
                    await onPageFetched(page, pageAnimes.Count);

                var hasNextPage = doc.RootElement.TryGetProperty("pagination", out var pagination) &&
                                  pagination.TryGetProperty("has_next_page", out var hnp) &&
                                  hnp.GetBoolean();
                if (!hasNextPage) break;

                page++;
                await Task.Delay(400, ct); // ~3 req/sec Jikan rate limit
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        return animes;
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            try
            {
                var response = await _httpClient.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    return response;

                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? "Unknown error";
                var error = new HttpRequestException(
                    $"Jikan returned HTTP {statusCode} ({reason}) for {url}.", null, response.StatusCode);

                if (!IsTransientStatusCode(statusCode) || attempt == MaxRequestAttempts)
                {
                    response.Dispose();
                    throw error;
                }

                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
                response.Dispose();
                lastError = error;
                _logger.LogWarning(error, "Jikan request failed (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                    attempt, MaxRequestAttempts, delay);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == null && attempt < MaxRequestAttempts)
            {
                lastError = ex;
                var delay = TimeSpan.FromSeconds(attempt * 2);
                _logger.LogWarning(ex, "Could not reach Jikan (attempt {Attempt}/{MaxAttempts}); retrying in {Delay}",
                    attempt, MaxRequestAttempts, delay);
                await Task.Delay(delay, ct);
            }
        }

        throw new HttpRequestException($"Jikan request failed after {MaxRequestAttempts} attempts: {url}", lastError);
    }

    private static bool IsTransientStatusCode(int statusCode) =>
        statusCode == StatusCodes.Status429TooManyRequests || statusCode >= StatusCodes.Status500InternalServerError;

    public async Task<List<Anime>> SearchAsync(string query)
    {
        var animes = new List<Anime>();
        try
        {
            var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(query)}&limit=25");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                foreach (var anime in data.EnumerateArray())
                    animes.Add(MapFromJikan(anime));
            }
        }
        catch { }
        return animes;
    }

    public async Task<Anime?> GetByIdAsync(int id, bool forceRefresh = false)
    {
        try
        {
            var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime/{id}");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("data");
                return MapFromJikan(data);
            }
        }
        catch { }
        return null;
    }

    public async Task<List<AnimeRelation>> GetRelationsAsync(int malId)
    {
        var relations = new List<AnimeRelation>();
        try
        {
            var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime/{malId}/relations");
            if (!response.IsSuccessStatusCode) return relations;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return relations;

            foreach (var rel in data.EnumerateArray())
            {
                var relType = rel.TryGetProperty("relation", out var rt) ? rt.GetString() ?? "" : "";
                if (!rel.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    var entryType = entry.TryGetProperty("type", out var et) ? et.GetString() : null;
                    if (entryType != "anime") continue;
                    var entryMalId = entry.TryGetProperty("mal_id", out var mid) ? mid.GetInt32() : 0;
                    var name = entry.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    if (entryMalId > 0)
                        relations.Add(new AnimeRelation { RelationType = relType, AnimeId = entryMalId, Name = name });
                }
            }
        }
        catch { }
        return relations;
    }

    public Task<List<AnimeRelation>> GetRecommendationsAsync(int animeId) => Task.FromResult(new List<AnimeRelation>());
    public Task<List<AnimeRelation>> GetRelatedAnimeAsync(int animeId) => Task.FromResult(new List<AnimeRelation>());
    public Task<List<Anime>> GetRecentAsync(int skip, int take) => Task.FromResult(new List<Anime>());
    public Task<int> GetTotalCountAsync() => Task.FromResult(0);
    public Task<List<Anime>> GetFilteredAsync(FilterState state, int skip, int take) => Task.FromResult(new List<Anime>());
    public Task<int> GetFilteredCountAsync(FilterState state) => Task.FromResult(0);
    public Task<List<string>> GetGenreNamesAsync() => Task.FromResult(new List<string>());
    public Task<List<string>> GetThemeNamesAsync() => Task.FromResult(new List<string>());
    public Task<List<string>> GetDemographicNamesAsync() => Task.FromResult(new List<string>());

    public Task<List<Genre>> GetAllGenresAsync()
    {
        var genres = new List<Genre>
        {
            new() { Id = 1,  Name = "Action" },
            new() { Id = 2,  Name = "Adventure" },
            new() { Id = 3,  Name = "Comedy" },
            new() { Id = 4,  Name = "Drama" },
            new() { Id = 5,  Name = "Fantasy" },
            new() { Id = 6,  Name = "Horror" },
            new() { Id = 7,  Name = "Mystery" },
            new() { Id = 8,  Name = "Romance" },
            new() { Id = 9,  Name = "Sci-Fi" },
            new() { Id = 10, Name = "Slice of Life" },
            new() { Id = 11, Name = "Sports" },
            new() { Id = 12, Name = "Supernatural" },
            new() { Id = 13, Name = "Thriller" },
        };
        return Task.FromResult(genres);
    }

    public Task<List<Theme>> GetAllThemesAsync()
    {
        var themes = new List<Theme>
        {
            new() { Id = 1,  Name = "Isekai" },
            new() { Id = 2,  Name = "Mecha" },
            new() { Id = 3,  Name = "Harem" },
            new() { Id = 4,  Name = "Magic" },
            new() { Id = 5,  Name = "School" },
            new() { Id = 6,  Name = "Military" },
            new() { Id = 7,  Name = "Historical" },
            new() { Id = 8,  Name = "Psychological" },
            new() { Id = 9,  Name = "Ecchi" },
            new() { Id = 10, Name = "Music" },
            new() { Id = 11, Name = "Parody" },
            new() { Id = 12, Name = "Samurai" },
            new() { Id = 13, Name = "Space" },
            new() { Id = 14, Name = "Vampire" },
        };
        return Task.FromResult(themes);
    }

    public Task<List<Demographic>> GetAllDemographicsAsync()
    {
        var demographics = new List<Demographic>
        {
            new() { Id = 1, Name = "Shounen" },
            new() { Id = 2, Name = "Shoujo" },
            new() { Id = 3, Name = "Seinen" },
            new() { Id = 4, Name = "Josei" },
            new() { Id = 5, Name = "Kids" },
        };
        return Task.FromResult(demographics);
    }

    internal Anime MapFromJikan(JsonElement element)
    {
        var titleEnglish = element.TryGetProperty("title_english", out var te) && te.ValueKind != JsonValueKind.Null
            ? te.GetString() : null;
        var title = !string.IsNullOrEmpty(titleEnglish)
            ? titleEnglish
            : element.GetProperty("title").GetString() ?? "Unknown";

        var imageUrl = element.TryGetProperty("images", out var images) &&
                       images.TryGetProperty("jpg", out var jpg) &&
                       jpg.TryGetProperty("large_image_url", out var largeUrl)
            ? largeUrl.GetString() : null;

        // Read genres, themes, and demographics into their own collections
        var genres = ParseTags<AnimeGenre, Genre>(element, "genres",
            name => new AnimeGenre { Genre = new Genre { Name = name } });
        var themes = ParseTags<AnimeTheme, Theme>(element, "themes",
            name => new AnimeTheme { Theme = new Theme { Name = name } });
        var demographics = ParseTags<AnimeDemographic, Demographic>(element, "demographics",
            name => new AnimeDemographic { Demographic = new Demographic { Name = name } });

        return new Anime
        {
            MALId = element.TryGetProperty("mal_id", out var malId) ? malId.GetInt32() : 0,
            Title = title,
            HasEnglishTitle = !string.IsNullOrWhiteSpace(titleEnglish),
            // Jikan reads MyAnimeList, so its English title is MyAnimeList's.
            DataSource = "MyAnimeList", SourceEnglishTitle = titleEnglish,
            OriginalTitle = element.TryGetProperty("title", out var jt) && jt.ValueKind != JsonValueKind.Null
                ? jt.GetString() : null,
            Synopsis = element.TryGetProperty("synopsis", out var syn) ? syn.GetString() : "No synopsis available.",
            ImageUrl = imageUrl,
            Type = element.TryGetProperty("type", out var t) ? t.GetString() : null,
            Episodes = element.TryGetProperty("episodes", out var ep) && ep.ValueKind != JsonValueKind.Null
                ? ep.GetInt32() : (int?)null,
            Rating = element.TryGetProperty("score", out var sc) && sc.ValueKind != JsonValueKind.Null
                ? (decimal?)sc.GetDouble() : null,
            Status = element.TryGetProperty("status", out var st) ? st.GetString() : null,
            StartDate = element.TryGetProperty("aired", out var aired) &&
                        aired.TryGetProperty("from", out var from) && from.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(from.GetString() ?? "") : (DateTime?)null,
            EndDate = element.TryGetProperty("aired", out var ended) &&
                      ended.TryGetProperty("to", out var to) && to.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(to.GetString() ?? "") : (DateTime?)null,
            SeasonId = 0,
            AnimeGenres = genres,
            AnimeThemes = themes,
            AnimeDemographics = demographics
        };
    }

    private static List<TLink> ParseTags<TLink, TTag>(JsonElement element, string field, Func<string, TLink> factory)
    {
        if (!element.TryGetProperty(field, out var fieldEl)) return [];
        var result = new List<TLink>();
        foreach (var item in fieldEl.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var gn)) continue;
            var name = gn.GetString();
            if (!string.IsNullOrEmpty(name))
                result.Add(factory(name));
        }
        return result;
    }

    public Task<List<Anime>> GetMostFavoritedAsync(int skip, int take)
        => Task.FromResult(new List<Anime>());

    public Task<int> GetMostFavoritedCountAsync()
        => Task.FromResult(0);

    public Task<List<Anime>> GetTopUpcomingAsync(int skip, int take)
        => Task.FromResult(new List<Anime>());

    public Task<int> GetTopUpcomingCountAsync()
        => Task.FromResult(0);

    public Task<List<Anime>> GetQuickFilterAsync(string preset, int skip, int take)
        => Task.FromResult(new List<Anime>());

    public Task<int> GetQuickFilterCountAsync(string preset)
        => Task.FromResult(0);

    public Task<List<QuickFilterRanking>> GetQuickFilterRankingsAsync(int animeId)
        => Task.FromResult(new List<QuickFilterRanking>());

    public Task<bool> SetPreferredImageAsync(int animeId, int imageId)
        => Task.FromResult(false);
}
