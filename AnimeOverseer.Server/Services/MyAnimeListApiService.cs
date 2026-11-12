using System.Net.Http.Headers;
using System.Text.Json;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>Optional secondary source. Configure MyAnimeList:ClientId outside source control.</summary>
public class MyAnimeListApiService(HttpClient httpClient, IConfiguration configuration, SettingsService settings)
{
    private const string BaseUrl = "https://api.myanimelist.net/v2";
    private const string Fields = "id,title,main_picture,alternative_titles,synopsis,media_type,status,num_episodes,mean,start_date,end_date,genres";

    public async Task<bool> IsConfiguredAsync()
        => !string.IsNullOrWhiteSpace(await ClientIdAsync());

    public async Task<List<Anime>> GetSeasonAsync(int year, string season, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync();
        EnsureConfigured(clientId);
        var result = new List<Anime>();
        var url = $"{BaseUrl}/anime/season/{year}/{season}?limit=100&fields={Fields}";
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var doc = await GetAsync(url, clientId, ct);
            result.AddRange(doc.RootElement.GetProperty("data").EnumerateArray().Select(x => Map(x.GetProperty("node"))));
            url = doc.RootElement.TryGetProperty("paging", out var paging) && paging.TryGetProperty("next", out var next) ? next.GetString() ?? "" : "";
        }
        return result;
    }

    public async Task<Anime?> GetByIdAsync(int malId, CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync();
        EnsureConfigured(clientId);
        using var doc = await GetAsync($"{BaseUrl}/anime/{malId}?fields={Fields}", clientId, ct);
        return Map(doc.RootElement);
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var clientId = await ClientIdAsync();
        EnsureConfigured(clientId);
        using var document = await GetAsync($"{BaseUrl}/anime/1?fields=id", clientId, ct);
    }

    private async Task<JsonDocument> GetAsync(string url, string clientId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-MAL-CLIENT-ID", clientId);
        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static Anime Map(JsonElement x)
    {
        var titles = x.TryGetProperty("alternative_titles", out var alternativeTitles) ? alternativeTitles : default;
        var english = Get(titles, "en");
        return new Anime
        {
        MALId = x.GetProperty("id").GetInt32(), Title = english ?? x.GetProperty("title").GetString() ?? "Unknown",
        HasEnglishTitle = !string.IsNullOrWhiteSpace(english),
        DataSource = "MyAnimeList", SourceEnglishTitle = english,
        OriginalTitle = Get(titles, "ja"),
        AlternativeTitles = GetAlternativeTitles(x),
        Synopsis = Get(x, "synopsis"), ImageUrl = x.TryGetProperty("main_picture", out var pic) ? Get(pic, "large") ?? Get(pic, "medium") : null,
        SourceImages = x.TryGetProperty("main_picture", out var picture) && (Get(picture, "large") ?? Get(picture, "medium")) is { Length: > 0 } poster
            ? [new SourceImage { Source = "MyAnimeList", Type = "Poster", Url = poster }]
            : [],
        Type = Get(x, "media_type")?.ToUpperInvariant() switch { "TV" => "TV", var type => type?.Replace('_', ' ') },
        Status = Get(x, "status")?.Replace('_', ' '), Episodes = Int(x, "num_episodes"),
        Rating = x.TryGetProperty("mean", out var mean) && mean.ValueKind != JsonValueKind.Null ? mean.GetDecimal() : null,
        StartDate = Date(x, "start_date"), EndDate = Date(x, "end_date"),
        AnimeGenres = x.TryGetProperty("genres", out var genres) ? genres.EnumerateArray().Select(g => new AnimeGenre { Genre = new Genre { Name = Get(g, "name") ?? "" } }).Where(g => g.Genre.Name.Length > 0).ToList() : []
        };
    }
    private static string? Get(JsonElement x, string name) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;
    private static List<string> GetAlternativeTitles(JsonElement anime)
    {
        if (!anime.TryGetProperty("alternative_titles", out var titles)) return [];
        var names = new List<string>();
        foreach (var field in new[] { "en", "ja" })
            if (Get(titles, field) is { Length: > 0 } name) names.Add(name);
        if (titles.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            names.AddRange(synonyms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
        return names;
    }
    private static int? Int(JsonElement x, string name) => x.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt32() : null;
    private static DateTime? Date(JsonElement x, string name) => DateTime.TryParse(Get(x, name), out var value) ? value : null;
    private async Task<string> ClientIdAsync()
        => await settings.GetAsync("myanimelist.clientId") ?? configuration["MyAnimeList:ClientId"] ?? "";
    private static void EnsureConfigured(string clientId) { if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("MyAnimeList client ID is not configured."); }
}
