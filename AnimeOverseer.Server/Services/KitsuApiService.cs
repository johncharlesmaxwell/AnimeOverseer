using System.Net.Http.Json;
using System.Text.Json;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

public class KitsuApiService : IAnimeDataSource
{
    private readonly HttpClient _httpClient;
    private const string ApiUrl = "https://kitsu.io/api/edge";

    public KitsuApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<Anime>> GetSeasonAnimes(int year, string season, bool forceRefresh = false)
    {
        var animes = new List<Anime>();
        // Kitsu doesn't have direct season filtering, so we'll return popular anime
        var response = await _httpClient.GetAsync($"{ApiUrl}/anime?filter[season]={season.ToLower()}&filter[year]={year}&limit=25");
        response.EnsureSuccessStatusCode();
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            foreach (var anime in data.EnumerateArray())
            {
                animes.Add(MapFromKitsu(anime, year, season));
            }
        }
        return animes;
    }

    public async Task<List<Anime>> SearchAsync(string query)
    {
        var animes = new List<Anime>();
        var response = await _httpClient.GetAsync($"{ApiUrl}/anime?filter[text]={Uri.EscapeDataString(query)}&limit=25");
        response.EnsureSuccessStatusCode();
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            foreach (var anime in data.EnumerateArray())
            {
                animes.Add(MapFromKitsu(anime, 0, ""));
            }
        }
        return animes;
    }

    public async Task<Anime?> GetByTitleAsync(string title)
    {
        var results = await SearchAsync(title);
        return results.FirstOrDefault();
    }

    public async Task<Anime?> GetByIdAsync(int id, bool forceRefresh = false)
    {
        var response = await _httpClient.GetAsync($"{ApiUrl}/anime/{id}");
        response.EnsureSuccessStatusCode();
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            return MapFromKitsu(data, 0, "");
        }
        return null;
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

    public Task<List<Genre>> GetAllGenresAsync() => Task.FromResult(new List<Genre>());
    public Task<List<Theme>> GetAllThemesAsync() => Task.FromResult(new List<Theme>());
    public Task<List<Demographic>> GetAllDemographicsAsync() => Task.FromResult(new List<Demographic>());

    private Anime MapFromKitsu(JsonElement element, int year, string season)
    {
        var attributes = element.GetProperty("attributes");
        var canonicalTitle = attributes.TryGetProperty("canonicalTitle", out var ct) ? ct.GetString() : null;
        var englishTitle = GetEnglishTitle(attributes);
        var title = englishTitle ?? canonicalTitle ?? "Unknown";
        var synopsis = attributes.TryGetProperty("synopsis", out var syn) ? syn.GetString() : "No synopsis available.";
        var coverImage = attributes.TryGetProperty("coverImage", out var ci) &&
                        ci.TryGetProperty("original", out var orig)
            ? orig.GetString()
            : null;

        return new Anime
        {
            KitsuId = element.TryGetProperty("id", out var id) && int.TryParse(id.GetString(), out var parsedId) ? parsedId : 0,
            Title = title,
            HasEnglishTitle = !string.IsNullOrWhiteSpace(englishTitle),
            DataSource = "Kitsu", SourceEnglishTitle = englishTitle,
            OriginalTitle = canonicalTitle,
            Synopsis = synopsis,
            ImageUrl = coverImage,
            Type = attributes.TryGetProperty("format", out var f) ? f.GetString()?.Replace("_", " ") : null,
            Episodes = attributes.TryGetProperty("episodeCount", out var ep) && ep.ValueKind != JsonValueKind.Null ? ep.GetInt32() : (int?)null,            
            Rating = attributes.TryGetProperty("averageRating", out var ar) && ar.ValueKind != JsonValueKind.Null
                ? double.TryParse(ar.GetString(), System.Globalization.NumberStyles.Any, 
                                  System.Globalization.CultureInfo.InvariantCulture, out var arVal)
                    ? (decimal?)((decimal)arVal / 10.0m)
                    : null
                : null,
            Status = attributes.TryGetProperty("status", out var st) ? st.GetString()?.Replace("_", " ") : null,
            StartDate = attributes.TryGetProperty("startDate", out var sd) && sd.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(sd.GetString() ?? "")
                : (DateTime?)null,
            EndDate = attributes.TryGetProperty("endDate", out var ed) && ed.ValueKind != JsonValueKind.Null
                ? DateTime.Parse(ed.GetString() ?? "")
                : (DateTime?)null,
            SeasonId = 0
        };
    }

    internal static string? GetEnglishTitle(JsonElement attributes)
    {
        if (!attributes.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Object)
            return null;

        // Not "en_jp": that is Kitsu's romanised Japanese title, not an English one.
        foreach (var locale in new[] { "en", "en_us" })
        {
            if (titles.TryGetProperty(locale, out var value) && value.ValueKind != JsonValueKind.Null &&
                !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        }

        return null;
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
