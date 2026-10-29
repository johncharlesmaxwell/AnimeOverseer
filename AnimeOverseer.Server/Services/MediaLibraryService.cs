using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

public class MediaLibraryService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
{
    private HashSet<string> _sonarrTitles = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _radarrTitles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefreshed = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsInSonarr(string? title) => !string.IsNullOrWhiteSpace(title) && _sonarrTitles.Contains(title.Trim());
    public bool IsInSonarr(Anime anime)
    {
        if (anime == null) return false;

        // Check exact matches first (existing logic)
        if (!string.IsNullOrWhiteSpace(anime.Title) && _sonarrTitles.Contains(anime.Title.Trim()))
            return true;
        if (!string.IsNullOrWhiteSpace(anime.OriginalTitle) && _sonarrTitles.Contains(anime.OriginalTitle.Trim()))
            return true;

        // Check enhanced matching for variations (like "Title" vs "Title Season 2")
        var animeTitles = GetTitles(anime);

        foreach (var sonarrTitle in _sonarrTitles)
        {
            foreach (var animeTitle in animeTitles)
            {
                if (TitlesMatch(animeTitle, sonarrTitle))
                    return true;
            }
        }

        return false;
    }

    public bool IsInRadarr(string? title) => !string.IsNullOrWhiteSpace(title) && _radarrTitles.Contains(title.Trim());
    public bool IsInRadarr(Anime anime)
    {
        if (anime == null) return false;

        // Check exact matches first (existing logic)
        if (!string.IsNullOrWhiteSpace(anime.Title) && _radarrTitles.Contains(anime.Title.Trim()))
            return true;
        if (!string.IsNullOrWhiteSpace(anime.OriginalTitle) && _radarrTitles.Contains(anime.OriginalTitle.Trim()))
            return true;

        // Check enhanced matching for variations (like "Title" vs "Title Season 2")
        var animeTitles = GetTitles(anime);

        foreach (var radarrTitle in _radarrTitles)
        {
            foreach (var animeTitle in animeTitles)
            {
                if (TitlesMatch(animeTitle, radarrTitle))
                    return true;
            }
        }

        return false;
    }


    internal static bool TitlesMatch(string title1, string title2)
    {
        if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2))
            return false;

        var t1 = TitleNormalizer.Normalize(title1);
        var t2 = TitleNormalizer.Normalize(title2);

        if (t1 == t2)
            return true;

        var shorter = t1.Length < t2.Length ? t1 : t2;
        var longer = t1.Length < t2.Length ? t2 : t1;

        // A title must end at a word boundary and the remaining text must be an
        // explicit season label. General substring matching made titles such as
        // "Blue Lock" match unrelated entries beginning with those words.
        if (longer.StartsWith(shorter, StringComparison.Ordinal) &&
            longer.Length > shorter.Length &&
            longer[shorter.Length] == ' ')
        {
            return TitleNormalizer.IsSeasonIndicator(longer[(shorter.Length + 1)..]);
        }

        return false;
    }

    private static IReadOnlyList<string> GetTitles(Anime anime) =>
        new[] { anime.Title, anime.OriginalTitle }
            .Concat(anime.AlternativeTitles)
            .Concat(anime.TitleAliases.Select(alias => alias.Title))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task EnsureRefreshedAsync()
    {
        if ((DateTime.UtcNow - _lastRefreshed).TotalMinutes < 10) return;
        await _lock.WaitAsync();
        try
        {
            if ((DateTime.UtcNow - _lastRefreshed).TotalMinutes < 10) return;
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
            // SettingsService shares this scope's DbContext. Keeping these
            // lookups sequential avoids concurrent EF operations; the HTTP data
            // is already cached for ten minutes after the first lookup.
            await RefreshSonarrAsync(settings);
            await RefreshRadarrAsync(settings);
            _lastRefreshed = DateTime.UtcNow;
        }
        finally { _lock.Release(); }
    }

    public void Invalidate() => _lastRefreshed = DateTime.MinValue;

    private async Task RefreshSonarrAsync(SettingsService settings)
    {
        try
        {
            var url = await settings.GetAsync("sonarr.url");
            var key = await settings.GetAsync("sonarr.apikey");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return;

            url = NormalizeUrl(url);
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("X-Api-Key", key);

            var resp = await client.GetAsync($"{url}/api/v3/series");
            if (!resp.IsSuccessStatusCode) return;

            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>();
            if (arr == null) return;

            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr.OfType<JsonObject>())
            {
                var t = item["title"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(t)) titles.Add(t.Trim());
            }
            _sonarrTitles = titles;
        }
        catch { /* silently ignore — indicator simply won't show if Sonarr is unreachable */ }
    }

    private async Task RefreshRadarrAsync(SettingsService settings)
    {
        try
        {
            var url = await settings.GetAsync("radarr.url");
            var key = await settings.GetAsync("radarr.apikey");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key)) return;

            url = NormalizeUrl(url);
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("X-Api-Key", key);

            var resp = await client.GetAsync($"{url}/api/v3/movie");
            if (!resp.IsSuccessStatusCode) return;

            var arr = await resp.Content.ReadFromJsonAsync<JsonArray>();
            if (arr == null) return;

            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr.OfType<JsonObject>())
            {
                var t = item["title"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(t)) titles.Add(t.Trim());
            }
            _radarrTitles = titles;
        }
        catch { /* silently ignore */ }
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url;
    }
}
