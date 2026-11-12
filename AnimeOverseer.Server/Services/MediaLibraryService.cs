using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

public class MediaLibraryService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
{
    private LibraryTitles _sonarrTitles = LibraryTitles.Empty;
    private LibraryTitles _radarrTitles = LibraryTitles.Empty;
    private DateTime _lastRefreshed = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool IsInSonarr(string? title) => _sonarrTitles.ContainsExact(title);
    public bool IsInSonarr(Anime anime) => anime != null && _sonarrTitles.Matches(anime);

    public bool IsInRadarr(string? title) => _radarrTitles.ContainsExact(title);
    public bool IsInRadarr(Anime anime) => anime != null && _radarrTitles.Matches(anime);

    internal static bool TitlesMatch(string title1, string title2)
    {
        if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2))
            return false;

        var t1 = TitleNormalizer.Normalize(title1);
        var t2 = TitleNormalizer.Normalize(title2);

        if (t1 == t2)
            return true;

        // Only an explicit trailing season label may differ ("Season 2", "2nd Cour",
        // "The Final Season"...). General prefix matching made titles such as "Blue Lock"
        // match unrelated entries beginning with those words. A library entry with no
        // label covers every season; two labels must name the same season and part.
        var (base1, label1) = TitleNormalizer.SplitSeasonSuffix(t1);
        var (base2, label2) = TitleNormalizer.SplitSeasonSuffix(t2);
        if (base1.Length == 0 || base1 != base2)
            return false;
        return label1 is null || label2 is null || label1 == label2;
    }

    /// <summary>
    /// Library titles indexed by their name without a trailing season label, so a lookup
    /// splits each anime title once instead of comparing it with every library title.
    /// Matches exactly what <see cref="TitlesMatch"/> accepts.
    /// </summary>
    internal sealed class LibraryTitles
    {
        public static readonly LibraryTitles Empty = new([]);

        private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _normalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Installment?>> _labelsByBase = new(StringComparer.Ordinal);

        public LibraryTitles(IEnumerable<string> titles)
        {
            foreach (var title in titles.Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title.Trim()))
            {
                _exact.Add(title);
                var normalized = TitleNormalizer.Normalize(title);
                if (normalized.Length == 0) continue;
                _normalized.Add(normalized);
                var (name, label) = TitleNormalizer.SplitSeasonSuffix(normalized);
                if (name.Length == 0) continue;
                if (!_labelsByBase.TryGetValue(name, out var labels)) _labelsByBase[name] = labels = [];
                labels.Add(label);
            }
        }

        public bool ContainsExact(string? title) => !string.IsNullOrWhiteSpace(title) && _exact.Contains(title.Trim());

        public bool Matches(Anime anime)
            => ContainsExact(anime.Title) || ContainsExact(anime.OriginalTitle) || GetTitles(anime).Any(Matches);

        public bool Matches(string title)
        {
            var normalized = TitleNormalizer.Normalize(title);
            if (normalized.Length == 0) return false;
            if (_normalized.Contains(normalized)) return true;
            var (name, label) = TitleNormalizer.SplitSeasonSuffix(normalized);
            return name.Length > 0 && _labelsByBase.TryGetValue(name, out var labels)
                && labels.Any(libraryLabel => libraryLabel is null || label is null || libraryLabel == label);
        }
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

            _sonarrTitles = new LibraryTitles(arr.OfType<JsonObject>().Select(item => item["title"]?.GetValue<string>() ?? string.Empty));
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

            _radarrTitles = new LibraryTitles(arr.OfType<JsonObject>().Select(item => item["title"]?.GetValue<string>() ?? string.Empty));
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
