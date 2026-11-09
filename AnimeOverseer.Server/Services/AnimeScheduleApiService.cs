using System.Net.Http.Headers;
using System.Text.Json;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>AnimeSchedule v3 catalogue fallback, authenticated with the saved OAuth token.</summary>
public class AnimeScheduleApiService(HttpClient httpClient, SettingsService settings)
{
    private const string BaseUrl = "https://animeschedule.net/api/v3";
    private const string ImageBaseUrl = "https://img.animeschedule.net/production/assets/public/img/";

    public async Task<bool> IsConfiguredAsync()
        => !string.IsNullOrWhiteSpace(await settings.GetAsync("animeschedule.applicationToken"));

    public async Task<List<Anime>> GetSeasonAsync(int year, string season, CancellationToken ct = default)
    {
        var token = await settings.GetAsync("animeschedule.applicationToken");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("AnimeSchedule is not connected.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/anime?years={year}&seasons={Uri.EscapeDataString(season)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return GetItems(document.RootElement).Select(Map).Where(x => x.MALId is > 0).ToList();
    }

    public async Task TestConnectionAsync(CancellationToken ct = default)
    {
        var token = await settings.GetAsync("animeschedule.applicationToken");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("AnimeSchedule is not connected.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/anime?page=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static IEnumerable<JsonElement> GetItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        foreach (var name in new[] { "anime", "data", "items" })
            if (root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array)
                return items.EnumerateArray().ToArray();
        return [];
    }

    private static Anime Map(JsonElement value)
    {
        var image = Get(value, "imageVersionRoute");
        var names = value.TryGetProperty("names", out var valueNames) ? valueNames : default;
        var english = Get(names, "english");
        return new Anime
        {
            MALId = GetMalId(value),
            Title = english ?? Get(value, "title") ?? Get(value, "route") ?? "Unknown",
            HasEnglishTitle = !string.IsNullOrWhiteSpace(english),
            DataSource = "AnimeSchedule", SourceEnglishTitle = english,
            OriginalTitle = Get(names, "native"),
            AlternativeTitles = value.TryGetProperty("names", out var alternativeNames) ? Names(alternativeNames) : [],
            Synopsis = Get(value, "description"),
            ImageUrl = string.IsNullOrWhiteSpace(image) ? null : ImageBaseUrl + image.TrimStart('/'),
            SourceImages = string.IsNullOrWhiteSpace(image) ? [] : [new SourceImage { Source = "AnimeSchedule", Type = "Poster", Url = ImageBaseUrl + image.TrimStart('/') }],
            Type = value.TryGetProperty("mediaTypes", out var mediaTypes) && mediaTypes.ValueKind == JsonValueKind.Array
                ? mediaTypes.EnumerateArray().Select(x => Get(x, "name")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                : null,
            Status = Get(value, "status"),
            Episodes = Int(value, "episodes"),
            Duration = Int(value, "lengthMin"),
            Rating = value.TryGetProperty("stats", out var stats) && stats.TryGetProperty("averageScore", out var score) && score.TryGetDecimal(out var rating) ? rating : null,
            StartDate = Date(value, "premier"),
            AnimeGenres = Categories(value, "genres")
        };
    }

    private static List<AnimeGenre> Categories(JsonElement value, string property)
        => value.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(x => Get(x, "name")).Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => new AnimeGenre { Genre = new Genre { Name = x! } }).ToList()
            : [];

    private static List<string> Names(JsonElement value)
    {
        var names = new[] { "english", "romaji", "native", "abbreviation" }
            .Select(name => Get(value, name)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();
        if (value.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
            names.AddRange(synonyms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
        return names;
    }

    private static int? GetMalId(JsonElement value)
    {
        if (!value.TryGetProperty("websites", out var websites)) return null;
        var url = Get(websites, "mal");
        return url?.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => int.TryParse(segment, out var id) ? (int?)id : null)
            .FirstOrDefault(id => id is > 0);
    }

    private static string? Get(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
    private static int? Int(JsonElement value, string property)
        => value.TryGetProperty(property, out var item) && item.TryGetInt32(out var number) ? number : null;
    private static DateTime? Date(JsonElement value, string property)
        => DateTime.TryParse(Get(value, property), out var date) ? date : null;
}
