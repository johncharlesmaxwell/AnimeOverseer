using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace AnimeOverseer.Server.Services;

/// <summary>The database is the catalogue identity boundary; provider IDs are optional metadata.</summary>
public class AnimeCacheService(AnimeDbContext db, AniListApiService aniList, JikanApiService jikan, ImageCacheService imageCache, IMemoryCache memoryCache) : IAnimeDataSource
{
    private static readonly string[] AllSeasons = ["spring", "summer", "fall", "winter"];
    private const string GenreNamesCacheKey = "catalogue:genre-names";
    private const string ThemeNamesCacheKey = "catalogue:theme-names";
    private const string DemographicNamesCacheKey = "catalogue:demographic-names";
    private const string TopUpcomingIdsSettingKey = "catalogue:top-upcoming-anilist-ids";
    private const string QuickFilterIdsSettingKeyPrefix = "catalogue:quick-filter-anilist-ids:";
    private const int TopUpcomingLimit = 1_000;

    // Catalogue cards need tags for local NSFW/library filtering, but never title
    // aliases or every artwork variant. Split queries avoid multiplying each card
    // by its tags and images in one large joined result set.
    private IQueryable<Anime> CardQuery() => db.Animes.AsNoTracking().AsSplitQuery()
        .Include(a => a.Season)
        .Include(a => a.Images.Where(image => image.Id == image.Anime.PreferredImageId))
        .Include(a => a.AnimeGenres).ThenInclude(x => x.Genre)
        .Include(a => a.AnimeThemes).ThenInclude(x => x.Theme)
        .Include(a => a.AnimeDemographics).ThenInclude(x => x.Demographic);

    private IQueryable<Anime> DetailQuery() => db.Animes.AsNoTracking().AsSplitQuery()
        .Include(a => a.Season).Include(a => a.Images).Include(a => a.TitleAliases)
        .Include(a => a.AnimeGenres).ThenInclude(x => x.Genre)
        .Include(a => a.AnimeThemes).ThenInclude(x => x.Theme)
        .Include(a => a.AnimeDemographics).ThenInclude(x => x.Demographic);

    public async Task<List<Anime>> GetSeasonAnimes(int year, string season, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            var cached = await DetailQuery().Where(a => a.Season.Year == year && a.Season.Name == season).ToListAsync();
            if (cached.Count > 0) return cached;
        }
        await FetchAndCacheSeasonsAsync(year, [season]);
        return await DetailQuery().Where(a => a.Season.Year == year && a.Season.Name == season).ToListAsync();
    }

    public async Task<List<Anime>> SearchAsync(string search)
    {
        var local = await DetailQuery().Where(a => a.Title.Contains(search) || (a.OriginalTitle != null && a.OriginalTitle.Contains(search))).Take(50).ToListAsync();
        try
        {
            var remote = await aniList.SearchAsync(search);
            await UpsertAsync(remote, null);
            var ids = remote.Where(a => a.AniListId is > 0).Select(a => a.AniListId!.Value).ToList();
            var fetched = await DetailQuery().Where(a => a.AniListId != null && ids.Contains(a.AniListId.Value)).Take(50).ToListAsync();
            return local.Concat(fetched).GroupBy(a => a.Id).Select(g => g.First()).OrderBy(a => a.Title).ToList();
        }
        catch { return local; }
    }

    // id is always this application's Anime.Id, never a provider ID.
    public async Task<Anime?> GetByIdAsync(int id, bool forceRefresh = false)
    {
        var cached = await DetailQuery().FirstOrDefaultAsync(a => a.Id == id);
        if (cached == null || !forceRefresh || cached.AniListId is not > 0) return cached;
        var remote = await aniList.GetByAniListIdAsync(cached.AniListId.Value);
        if (remote == null) return cached;
        await UpsertAsync([remote], cached.SeasonId);
        return await DetailQuery().FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<bool> SetPreferredImageAsync(int animeId, int imageId)
    {
        var anime = await db.Animes.FirstOrDefaultAsync(item => item.Id == animeId);
        var imageExists = await db.AnimeImages.AnyAsync(image => image.Id == imageId && image.AnimeId == animeId);
        if (anime is null || !imageExists) return false;

        anime.PreferredImageId = imageId;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<List<int>> FetchAndCacheSeasonsAsync(int year, string[] seasons, Func<int, int, Task>? onPageFetched = null, CancellationToken ct = default)
    {
        var syncedIds = new List<int>();
        foreach (var season in seasons)
        {
            var remote = await aniList.GetSeasonAnimesAsync(year, season, onPageFetched, ct);
            var seasonRow = await GetOrCreateSeasonAsync(season, year, ct);
            syncedIds.AddRange(await UpsertAsync(remote, seasonRow.Id, ct: ct));
        }
        return syncedIds.Distinct().ToList();
    }

    /// <summary>Fetches a catalogue from one explicitly selected provider and persists it.</summary>
    public async Task<List<int>> FetchAndCacheSeasonsFromAsync(
        int year,
        string[] seasons,
        Func<int, string, CancellationToken, Task<List<Anime>>> fetch,
        Func<int, Task>? onSeasonFetched = null,
        CancellationToken ct = default)
    {
        var syncedIds = new List<int>();
        foreach (var season in seasons)
        {
            ct.ThrowIfCancellationRequested();
            var remote = await fetch(year, season, ct);
            if (onSeasonFetched is not null) await onSeasonFetched(remote.Count);
            var seasonRow = await GetOrCreateSeasonAsync(season, year, ct);
            syncedIds.AddRange(await UpsertAsync(remote, seasonRow.Id, ct: ct));
        }
        return syncedIds.Distinct().ToList();
    }

    public async Task<List<int>> FetchAndCacheCurrentlyAiringAsync(Func<string, int, Task>? onRankingCached = null, CancellationToken ct = default)
    {
        var syncedIds = new List<int>();
        var airing = await aniList.GetCurrentAsync(false, ct: ct);
        var upcoming = await aniList.GetCurrentAsync(true, ct: ct, maximumItems: TopUpcomingLimit);
        foreach (var group in airing.Concat(upcoming).GroupBy(a => (Year: a.StartDate?.Year ?? DateTime.UtcNow.Year, Season: SeasonName(a.StartDate))))
        {
            var season = await GetOrCreateSeasonAsync(group.Key.Season, group.Key.Year, ct);
            syncedIds.AddRange(await UpsertAsync(group.ToList(), season.Id, ct: ct));
        }

        await SaveRankedIdsAsync("top-upcoming", upcoming, ct);
        await SaveRankedIdsAsync("top-airing", airing, ct);
        if (onRankingCached is not null)
        {
            await onRankingCached("Top Airing", airing.Count);
            await onRankingCached("Top Upcoming", upcoming.Count);
        }

        var rankings = new[]
        {
            (Preset: "top-tv", Format: "TV", Sort: "POPULARITY_DESC"),
            (Preset: "top-movies", Format: "MOVIE", Sort: "POPULARITY_DESC"),
            (Preset: "top-ovas", Format: "OVA", Sort: "POPULARITY_DESC"),
            (Preset: "top-onas", Format: "ONA", Sort: "POPULARITY_DESC"),
            (Preset: "top-specials", Format: "SPECIAL", Sort: "POPULARITY_DESC"),
            (Preset: "most-popular", Format: (string?)null, Sort: "POPULARITY_DESC"),
            (Preset: "most-favorited", Format: (string?)null, Sort: "FAVOURITES_DESC")
        };
        foreach (var ranking in rankings)
        {
            var animes = await aniList.GetTopAnimeAsync(ranking.Format, sort: ranking.Sort, ct: ct, maximumItems: TopUpcomingLimit);
            foreach (var group in animes.GroupBy(anime => (Year: anime.StartDate?.Year ?? DateTime.UtcNow.Year, Season: SeasonName(anime.StartDate))))
            {
                var season = await GetOrCreateSeasonAsync(group.Key.Season, group.Key.Year, ct);
                syncedIds.AddRange(await UpsertAsync(group.ToList(), season.Id, ct: ct));
            }
            await SaveRankedIdsAsync(ranking.Preset, animes, ct);
            if (onRankingCached is not null) await onRankingCached(QuickFilterLabel(ranking.Preset), animes.Count);
        }
        return syncedIds.Distinct().ToList();
    }

    public async Task<List<Anime>> GetTopUpcomingAsync(int skip, int take)
    {
        var ids = await GetTopUpcomingIdsAsync();
        var pageIds = ids.Skip(skip).Take(take).ToArray();
        if (pageIds.Length == 0) return [];
        var animes = await CardQuery().Where(anime => anime.AniListId != null && pageIds.Contains(anime.AniListId.Value)).ToListAsync();
        var order = pageIds.Select((id, index) => new { id, index }).ToDictionary(item => item.id, item => item.index);
        return animes.OrderBy(anime => order[anime.AniListId!.Value]).ToList();
    }

    public async Task<int> GetTopUpcomingCountAsync() => (await GetTopUpcomingIdsAsync()).Count;

    public Task<List<Anime>> GetQuickFilterAsync(string preset, int skip, int take) => GetRankedAnimeAsync(preset, skip, take);
    public async Task<int> GetQuickFilterCountAsync(string preset) => (await GetRankedIdsAsync(preset)).Count;

    public async Task<List<QuickFilterRanking>> GetQuickFilterRankingsAsync(int animeId)
    {
        var aniListId = await db.Animes.AsNoTracking().Where(anime => anime.Id == animeId).Select(anime => anime.AniListId).FirstOrDefaultAsync();
        if (aniListId is not > 0) return [];
        var presets = new[] { "top-airing", "top-upcoming", "top-tv", "top-movies", "top-ovas", "top-onas", "top-specials", "most-popular", "most-favorited" };
        var rankings = new List<QuickFilterRanking>();
        foreach (var preset in presets)
        {
            var rank = (await GetRankedIdsAsync(preset)).IndexOf(aniListId.Value);
            if (rank >= 0) rankings.Add(new QuickFilterRanking(preset, QuickFilterLabel(preset), rank + 1));
        }
        return rankings;
    }

    private static string QuickFilterLabel(string preset) => preset switch
    {
        "top-airing" => "Top Airing", "top-upcoming" => "Top Upcoming", "top-tv" => "Top TV Series",
        "top-movies" => "Top Movies", "top-ovas" => "Top OVAs", "top-onas" => "Top ONAs",
        "top-specials" => "Top Specials", "most-popular" => "Most Popular", "most-favorited" => "Most Favorited", _ => preset
    };

    private async Task SaveRankedIdsAsync(string preset, IEnumerable<Anime> animes, CancellationToken ct)
    {
        var key = preset == "top-upcoming" ? TopUpcomingIdsSettingKey : QuickFilterIdsSettingKeyPrefix + preset;
        var orderedIds = animes.Where(anime => anime.AniListId is > 0).Select(anime => anime.AniListId!.Value).Distinct().ToArray();
        var setting = await db.AppSettings.FirstOrDefaultAsync(item => item.Key == key, ct);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = JsonSerializer.Serialize(orderedIds) });
        else setting.Value = JsonSerializer.Serialize(orderedIds);
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<Anime>> GetRankedAnimeAsync(string preset, int skip, int take)
    {
        var ids = await GetRankedIdsAsync(preset);
        var pageIds = ids.Skip(skip).Take(take).ToArray();
        if (pageIds.Length == 0) return [];
        var animes = await CardQuery().Where(anime => anime.AniListId != null && pageIds.Contains(anime.AniListId.Value)).ToListAsync();
        var order = pageIds.Select((id, index) => new { id, index }).ToDictionary(item => item.id, item => item.index);
        return animes.OrderBy(anime => order[anime.AniListId!.Value]).ToList();
    }

    private async Task<List<int>> GetTopUpcomingIdsAsync()
    {
        return await GetRankedIdsAsync("top-upcoming");
    }

    private async Task<List<int>> GetRankedIdsAsync(string preset)
    {
        var key = preset == "top-upcoming" ? TopUpcomingIdsSettingKey : QuickFilterIdsSettingKeyPrefix + preset;
        var value = await db.AppSettings.AsNoTracking().Where(item => item.Key == key).Select(item => item.Value).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(value)) return [];
        try { return JsonSerializer.Deserialize<List<int>>(value) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Returns recommendations already cached by the recommendation sync; never calls AniList.</summary>
    public Task<List<AnimeRelation>> GetRecommendationsAsync(int animeId)
        => (from recommendation in db.AnimeRecommendations.AsNoTracking()
            join recommendedAnime in db.Animes.AsNoTracking() on recommendation.RecommendedAnimeId equals recommendedAnime.Id
            where recommendation.SourceAnimeId == animeId
            orderby recommendation.Rating descending, recommendedAnime.Title
            select new AnimeRelation
            {
                AnimeId = recommendedAnime.Id,
                Name = recommendedAnime.Title,
                ImageUrl = recommendedAnime.ImageUrl,
                LocalImagePath = recommendedAnime.LocalImagePath
            })
            .ToListAsync();

    /// <summary>Returns locally cached relationship links from the AniList and Jikan relation sync.</summary>
    public async Task<List<AnimeRelation>> GetRelatedAnimeAsync(int animeId)
    {
        var rootMalId = await db.Animes.Where(anime => anime.Id == animeId).Select(anime => anime.MALId).FirstOrDefaultAsync();
        if (rootMalId is not > 0) return [];
        return await (from relation in db.CachedAnimeRelations.AsNoTracking()
                      join relatedAnime in db.Animes.AsNoTracking() on relation.RelatedMalId equals relatedAnime.MALId
                      where relation.RootMalId == rootMalId.Value
                      orderby relation.RelationType, relatedAnime.Title
                      select new AnimeRelation { AnimeId = relatedAnime.Id, Name = relatedAnime.Title, RelationType = relation.RelationType })
            .ToListAsync();
    }

    /// <summary>Refreshes locally displayable franchise links using AniList and Jikan.</summary>
    public async Task<int> FetchAndStoreRelationsAsync(int animeId, CancellationToken ct = default)
    {
        var root = await db.Animes.AsNoTracking().FirstOrDefaultAsync(anime => anime.Id == animeId, ct);
        if (root?.AniListId is not > 0 || root.MALId is not > 0) return 0;

        var aniListRelations = await aniList.GetRelationsAsync(root.AniListId.Value, ct);
        await UpsertAsync(aniListRelations.Select(item => item.Anime).ToList(), root.SeasonId, preserveExistingSeason: true, ct: ct);
        var jikanRelations = await jikan.GetRelationsAsync(root.MALId.Value);

        var relatedAniListIds = aniListRelations.Select(item => item.Anime.AniListId).Where(id => id is > 0).Select(id => id!.Value).ToArray();
        var relatedMalIds = jikanRelations.Select(item => item.AnimeId)
            .Concat(aniListRelations.Select(item => item.Anime.MALId).Where(id => id is > 0).Select(id => id!.Value))
            .Distinct().ToArray();
        var local = await db.Animes.AsNoTracking()
            .Where(anime => (anime.AniListId != null && relatedAniListIds.Contains(anime.AniListId.Value)) || (anime.MALId != null && relatedMalIds.Contains(anime.MALId.Value)))
            .Select(anime => new { anime.MALId, anime.AniListId, anime.Title }).ToListAsync(ct);

        var old = await db.CachedAnimeRelations.Where(item => item.RootMalId == root.MALId.Value).ToListAsync(ct);
        db.CachedAnimeRelations.RemoveRange(old);
        var links = new HashSet<(int MalId, string Type)>();
        void AddLink(int? malId, string relationType, string name)
        {
            relationType = NormalizeRelationType(relationType);
            if (malId is not > 0 || malId == root.MALId || !links.Add((malId.Value, relationType))) return;
            db.CachedAnimeRelations.Add(new CachedAnimeRelation { RootMalId = root.MALId.Value, RelatedMalId = malId.Value, RelationType = relationType, Name = name, CachedAt = DateTime.UtcNow });
        }
        foreach (var relation in aniListRelations)
        {
            var saved = local.FirstOrDefault(item => item.AniListId == relation.Anime.AniListId);
            AddLink(saved?.MALId, relation.RelationType, saved?.Title ?? relation.Anime.Title);
        }
        foreach (var relation in jikanRelations)
        {
            var saved = local.FirstOrDefault(item => item.MALId == relation.AnimeId);
            AddLink(saved?.MALId, relation.RelationType, saved?.Title ?? relation.Name);
        }
        await db.SaveChangesAsync(ct);
        return links.Count;
    }

    private static string NormalizeRelationType(string value)
        => string.IsNullOrWhiteSpace(value) ? "Related" : value.ToUpperInvariant() switch
        {
            "PREQUEL" => "Prequel",
            "SEQUEL" => "Sequel",
            "PARENT" => "Parent Story",
            "SIDE_STORY" => "Side Story",
            "ALTERNATIVE" => "Alternative Version",
            "SUMMARY" => "Summary",
            "FULL_STORY" => "Full Story",
            "CHARACTER" => "Character",
            "ADAPTATION" => "Adaptation",
            _ => string.Join(' ', value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()))
        };

    /// <summary>Refreshes the AniList recommendations for a locally stored anime.</summary>
    public async Task<int> FetchAndStoreRecommendationsAsync(int animeId, CancellationToken ct = default)
    {
        var root = await db.Animes.AsNoTracking().FirstOrDefaultAsync(anime => anime.Id == animeId, ct);
        if (root?.AniListId is not > 0) return 0;

        var remote = await aniList.GetRecommendationsAsync(root.AniListId.Value, ct);
        var candidates = remote.Select(item => item.Anime).ToList();
        await UpsertAsync(candidates, root.SeasonId, preserveExistingSeason: true, ct: ct);

        var aniListIds = candidates.Where(anime => anime.AniListId is > 0).Select(anime => anime.AniListId!.Value).ToArray();
        var saved = await db.Animes.Where(anime => anime.AniListId != null && aniListIds.Contains(anime.AniListId.Value))
            .Select(anime => new { anime.Id, AniListId = anime.AniListId!.Value }).ToListAsync(ct);
        // Older databases can contain multiple rows with the same external ID.
        // That should not abort the entire sync; use a stable local target until
        // the duplicate can be reviewed and merged separately.
        var localIds = saved
            .GroupBy(anime => anime.AniListId)
            .ToDictionary(group => group.Key, group => group.Min(anime => anime.Id));

        var old = await db.AnimeRecommendations.Where(item => item.SourceAnimeId == root.Id).ToListAsync(ct);
        db.AnimeRecommendations.RemoveRange(old);
        var now = DateTime.UtcNow;
        foreach (var item in remote.GroupBy(item => item.Anime.AniListId).Select(group => group.OrderByDescending(item => item.Rating).First()))
        {
            if (item.Anime.AniListId is not int aniListId || !localIds.TryGetValue(aniListId, out var recommendedId) || recommendedId == root.Id) continue;
            db.AnimeRecommendations.Add(new AnimeRecommendation { SourceAnimeId = root.Id, RecommendedAnimeId = recommendedId, Rating = item.Rating, CachedAt = now });
        }
        await db.SaveChangesAsync(ct);
        return localIds.Count;
    }

    public Task<List<Anime>> GetRecentAsync(int skip, int take) => CardQuery().OrderByDescending(a => a.StartDate).Skip(skip).Take(take).ToListAsync();
    public Task<int> GetTotalCountAsync() => db.Animes.CountAsync();
    public Task<List<Anime>> GetFilteredAsync(FilterState state, int skip, int take) => FilterQueryBuilder.Apply(CardQuery(), state).OrderByDescending(a => a.StartDate).Skip(skip).Take(take).ToListAsync();
    public Task<int> GetFilteredCountAsync(FilterState state) => FilterQueryBuilder.Apply(db.Animes.AsNoTracking(), state).CountAsync();
    public Task<List<string>> GetGenreNamesAsync() => GetCachedNamesAsync(GenreNamesCacheKey, db.Genres);
    public Task<List<string>> GetThemeNamesAsync() => GetCachedNamesAsync(ThemeNamesCacheKey, db.Themes);
    public Task<List<string>> GetDemographicNamesAsync() => GetCachedNamesAsync(DemographicNamesCacheKey, db.Demographics);
    public Task<List<Anime>> GetMostFavoritedAsync(int skip, int take) => CardQuery().OrderByDescending(a => a.Requests.Count).Skip(skip).Take(take).ToListAsync();
    public Task<int> GetMostFavoritedCountAsync() => db.Animes.CountAsync();
    public Task<List<Genre>> GetAllGenresAsync() => db.Genres.OrderBy(x => x.Name).ToListAsync();
    public Task<List<Theme>> GetAllThemesAsync() => db.Themes.OrderBy(x => x.Name).ToListAsync();
    public Task<List<Demographic>> GetAllDemographicsAsync() => db.Demographics.OrderBy(x => x.Name).ToListAsync();

    private Task<List<string>> GetCachedNamesAsync<TTag>(string key, IQueryable<TTag> tags) where TTag : class
        => memoryCache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return await tags.Select(tag => EF.Property<string>(tag, "Name")).OrderBy(name => name).ToListAsync();
        })!;

    private void InvalidateLookupCaches()
    {
        memoryCache.Remove(GenreNamesCacheKey);
        memoryCache.Remove(ThemeNamesCacheKey);
        memoryCache.Remove(DemographicNamesCacheKey);
    }

    /// <summary>Applies a human decision for a staged title/alias match.</summary>
    /// <summary>A person's decision on a pending review. Merge and Create are remembered for later imports.</summary>
    public Task<bool> ResolvePendingReviewAsync(int reviewId, string decision, int? candidateAnimeId = null, CancellationToken ct = default)
        => ResolveCoreAsync(reviewId, decision, candidateAnimeId, rememberDecision: true, ct);

    private async Task<bool> ResolveCoreAsync(int reviewId, string decision, int? candidateAnimeId, bool rememberDecision, CancellationToken ct)
    {
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.Status == "Pending", ct);
        if (review is null) return false;
        if (decision == "Defer")
        {
            // Snooze rather than drop: the next import of this source after the
            // snooze reopens this review instead of queueing a duplicate.
            review.Status = "Deferred";
            review.ResolvedAt = DateTime.UtcNow;
            review.DeferredUntil = review.ResolvedAt.Value.AddDays((await MatchingSettings.LoadAsync(db, ct)).DeferDays);
            await db.SaveChangesAsync(ct);
            return true;
        }

        var snapshot = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson);
        if (snapshot is null) return false;
        var source = FromSnapshot(snapshot);
        Anime target;
        if (decision == "Create")
        {
            target = new Anime { SeasonId = review.SeasonId };
            db.Animes.Add(target);
        }
        else if (decision == "Merge" && candidateAnimeId is > 0)
        {
            var candidateIds = JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [];
            if (!candidateIds.Contains(candidateAnimeId.Value)) return false;
            var existing = await db.Animes.Include(item => item.TitleAliases).Include(item => item.Images).Include(item => item.SourceTitles)
                .Include(item => item.AnimeGenres).ThenInclude(item => item.Genre)
                .Include(item => item.AnimeThemes).ThenInclude(item => item.Theme)
                .Include(item => item.AnimeDemographics).ThenInclude(item => item.Demographic)
                .FirstOrDefaultAsync(item => item.Id == candidateAnimeId.Value, ct);
            if (existing is null) return false;
            target = existing;
            review.PreMergeSnapshotJson = JsonSerializer.Serialize(ToRestorePoint(existing));
        }
        else return false;

        Copy(target, source);
        await StoreTitleAliasesAsync(target, source, ct);
        await db.SaveChangesAsync(ct);
        if (!string.IsNullOrWhiteSpace(source.ImageUrl)) target.LocalImagePath = await imageCache.CacheImageAsync(source.ImageUrl, target.Id);
        await StoreImagesAsync(target, source, ct);
        await ReplaceTagsAsync(target, source, ct);
        review.Status = decision == "Create" ? "Created" : "Merged";
        review.ResolvedAt = DateTime.UtcNow;
        review.ResolvedAnimeId = target.Id;
        if (rememberDecision)
        {
            // Creating says no candidate is this anime; merging says the chosen one is
            // and the others are not.
            var (aniListId, malId) = SourceIds(review, snapshot);
            foreach (var candidateId in JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [])
                if (candidateId != target.Id) await RememberAsync(aniListId, malId, candidateId, isSame: false, decision, ct);
            if (decision == "Merge") await RememberAsync(aniListId, malId, target.Id, isSame: true, decision, ct);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static (int? AniListId, int? MalId) SourceIds(PendingAnimeReview review, AnimeImportSnapshot snapshot)
        => (snapshot.AniListId is > 0 ? snapshot.AniListId : review.SourceAniListId, snapshot.MALId is > 0 ? snapshot.MALId : review.SourceMalId);

    /// <summary>Records one same/different answer, replacing any earlier answer for the pair.</summary>
    private async Task RememberAsync(int? aniListId, int? malId, int animeId, bool isSame, string origin, CancellationToken ct)
    {
        if (aniListId is not > 0 && malId is not > 0) return;
        if (!await db.Animes.AnyAsync(anime => anime.Id == animeId, ct)) return;
        var earlier = (await db.AnimeMatchDecisions.Where(decision => decision.AnimeId == animeId).ToListAsync(ct))
            .Where(decision => decision.AppliesTo(aniListId, malId));
        db.AnimeMatchDecisions.RemoveRange(earlier);
        db.AnimeMatchDecisions.Add(new AnimeMatchDecision
        {
            SourceAniListId = aniListId is > 0 ? aniListId : null, SourceMalId = malId is > 0 ? malId : null,
            AnimeId = animeId, IsSameAnime = isSame, Origin = origin
        });
    }

    private async Task<List<AnimeMatchDecision>> DecisionsForAsync(int? aniListId, int? malId, CancellationToken ct)
    {
        if (aniListId is not > 0 && malId is not > 0) return [];
        return await db.AnimeMatchDecisions.AsNoTracking()
            .Where(decision => (aniListId > 0 && decision.SourceAniListId == aniListId) || (malId > 0 && decision.SourceMalId == malId))
            .ToListAsync(ct);
    }

    /// <summary>Candidates a person has already said are a different anime from the source.</summary>
    private static List<Anime> WithoutRejected(IEnumerable<AnimeMatchDecision> decisions, Anime source, IEnumerable<Anime> candidates)
        => candidates.Where(candidate => !decisions.Any(decision =>
            !decision.IsSameAnime && decision.AnimeId == candidate.Id && decision.AppliesTo(source.AniListId, source.MALId))).ToList();

    private async Task<List<int>> UpsertAsync(List<Anime> incoming, int? fixedSeasonId, bool preserveExistingSeason = false, CancellationToken ct = default)
    {
        var aniListIds = incoming.Where(a => a.AniListId is > 0).Select(a => a.AniListId!.Value).Distinct().ToList();
        var malIds = incoming.Where(a => a.MALId is > 0).Select(a => a.MALId!.Value).Distinct().ToList();
        var sources = incoming.Where(a => a.AniListId is > 0 || a.MALId is > 0).GroupBy(Key).Select(g => g.First()).ToList();
        // Most syncs update provider IDs already in the catalogue. Start with
        // only those candidates; fall back to the complete alias scan only when
        // a source really needs title-based identity matching.
        var existingRows = await db.Animes.Include(a => a.TitleAliases)
            .Where(a => (a.AniListId != null && aniListIds.Contains(a.AniListId.Value)) ||
                        (a.MALId != null && malIds.Contains(a.MALId.Value)))
            .ToListAsync(ct);
        var resolvedProviderIds = existingRows.SelectMany(row => new[]
            { row.AniListId is > 0 ? $"a:{row.AniListId}" : null, row.MALId is > 0 ? $"m:{row.MALId}" : null })
            .Where(key => key is not null).Select(key => key!).ToHashSet();
        if (sources.Any(source => !resolvedProviderIds.Contains(Key(source)) &&
                                  (source.MALId is not > 0 || !resolvedProviderIds.Contains($"m:{source.MALId}"))))
            existingRows = await db.Animes.Include(a => a.TitleAliases).ToListAsync(ct);
        var existing = new Dictionary<string, Anime>();
        var queuedReviews = new List<PendingAnimeReview>();
        List<AnimeMatchDecision>? decisions = null;
        var names = new CatalogueNameIndex();
        foreach (var row in existingRows)
        {
            if (row.AniListId is > 0) existing[$"a:{row.AniListId}"] = row;
            if (row.MALId is > 0) existing[$"m:{row.MALId}"] = row;
            names.Add(row);
        }
        foreach (var source in sources)
        {
            if (!existing.TryGetValue(Key(source), out var target) &&
                !(source.MALId is > 0 && existing.TryGetValue($"m:{source.MALId}", out target)))
            {
                // A person's earlier answer for this source outranks title evidence.
                decisions ??= await db.AnimeMatchDecisions.AsNoTracking().ToListAsync(ct);
                var rememberedSame = decisions.FirstOrDefault(decision => decision.IsSameAnime && decision.AppliesTo(source.AniListId, source.MALId));
                var candidates = rememberedSame is not null && existingRows.FirstOrDefault(row => row.Id == rememberedSame.AnimeId) is { } same
                    ? [same]
                    : WithoutRejected(decisions, source, names.FindCandidates(source));
                if (rememberedSame is not null && candidates.Count == 1 && candidates[0].Id == rememberedSame.AnimeId)
                    target = candidates[0];
                else if (candidates.Count > 0)
                {
                    // Before asking a person, let AniList bridge the provider IDs:
                    // a shared ID confirms the row, a differing one rules it out.
                    await CompleteProviderIdsAsync(source, candidates, ct);
                    if (!TryGetByProviderIds(existing, source, out target))
                    {
                        candidates = ViableCandidates(source, candidates);
                        if (candidates.Count > 0)
                        {
                            // A title is search evidence, not an identity.  This avoids a
                            // franchise synonym (for example a season-one title attached to
                            // season four) silently overwriting a different catalogue row.
                            if (await QueueForReviewAsync(source, fixedSeasonId, candidates, ct) is { } queued) queuedReviews.Add(queued);
                            continue;
                        }
                    }
                }
                if (target is null)
                {
                    var seasonId = preserveExistingSeason
                        ? await SeasonForSourceAsync(source, fixedSeasonId, ct)
                        : fixedSeasonId ?? await UnknownSeasonIdAsync(ct);
                    target = new Anime { AniListId = source.AniListId, SeasonId = seasonId };
                    db.Animes.Add(target);
                }
            }
            Copy(target, source);
            await StoreTitleAliasesAsync(target, source, ct);
            if (source.AniListId is > 0) existing[$"a:{source.AniListId}"] = target;
            if (source.MALId is > 0) existing[$"m:{source.MALId}"] = target;
            names.Add(source, target);
            // Recommendation and relation lookups enrich many existing titles.
            // They must not move a title into the source title's season.
            if (fixedSeasonId is > 0 && !preserveExistingSeason) target.SeasonId = fixedSeasonId.Value;
            target.CachedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        var persistedSources = sources.Where(source => existing.ContainsKey(Key(source))).ToList();
        var syncedIds = persistedSources.Select(source => existing[Key(source)].Id).Distinct().ToList();
        foreach (var source in persistedSources)
        {
            var target = existing[Key(source)];
            if (!string.IsNullOrWhiteSpace(source.ImageUrl)) target.LocalImagePath = await imageCache.CacheImageAsync(source.ImageUrl, target.Id);
            await StoreImagesAsync(target, source, ct);
            await ReplaceTagsAsync(target, source, ct);
            // The same title may occur in more than one provider season. Keep
            // the next replacement independent of the joins tracked for this one.
            db.ChangeTracker.Clear();
        }
        if (queuedReviews.Count > 0)
        {
            // Apply decision bands only after the batch is persisted, so a resolution
            // never interleaves with the batch's tracked rows.
            var settings = await MatchingSettings.LoadAsync(db, ct);
            foreach (var review in queuedReviews)
            {
                db.ChangeTracker.Clear();
                if (await ApplySuggestionAsync(review.Id, settings, ct) is not PendingReviewRecheckOutcome.Unchanged &&
                    await db.PendingAnimeReviews.Where(item => item.Id == review.Id).Select(item => item.ResolvedAnimeId).FirstOrDefaultAsync(ct) is int resolvedId &&
                    !syncedIds.Contains(resolvedId))
                    syncedIds.Add(resolvedId);
            }
        }
        return syncedIds;
    }

    private async Task<int> SeasonForSourceAsync(Anime source, int? fallbackSeasonId, CancellationToken ct)
    {
        if (source.StartDate?.Year is int year and > 0)
            return (await GetOrCreateSeasonAsync(SeasonName(source.StartDate), year, ct)).Id;
        return fallbackSeasonId ?? await UnknownSeasonIdAsync(ct);
    }

    private static string Key(Anime anime) => anime.AniListId is > 0 ? $"a:{anime.AniListId}" : $"m:{anime.MALId}";

    private async Task<PendingAnimeReview?> QueueForReviewAsync(Anime source, int? fixedSeasonId, IReadOnlyCollection<Anime> candidates, CancellationToken ct)
    {
        var prior = await db.PendingAnimeReviews
            .Where(review => (review.Status == "Pending" || review.Status == "Deferred") &&
                ((source.AniListId.HasValue && source.AniListId.Value > 0 && review.SourceAniListId == source.AniListId) ||
                 (source.MALId.HasValue && source.MALId.Value > 0 && review.SourceMalId == source.MALId)))
            .OrderBy(review => review.Status == "Pending" ? 0 : 1).ThenByDescending(review => review.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (prior?.Status == "Pending" || prior?.DeferredUntil > DateTime.UtcNow) return null;

        var reason = candidates.Count > 1
            ? "More than one title/alias candidate was found"
            : "Title or alias match needs confirmation";
        await LoadTagsAsync(candidates, ct);
        var matches = ScoreCandidates(source, candidates, (await MatchingSettings.LoadAsync(db, ct)).Scoring, await NameUsageAsync(ct));
        // An expired snooze reopens the deferred review with the fresh import.
        var review = prior ?? new PendingAnimeReview { SeasonId = fixedSeasonId ?? await UnknownSeasonIdAsync(ct), SourceName = "Catalogue import" };
        review.SourceAniListId = source.AniListId;
        review.SourceMalId = source.MALId;
        review.Reason = prior is null ? reason : $"{reason} · reopened after being deferred";
        review.PayloadJson = JsonSerializer.Serialize(ToSnapshot(source));
        review.CandidateAnimeIdsJson = JsonSerializer.Serialize(candidates.Select(candidate => candidate.Id).Distinct());
        review.MatchScore = matches.FirstOrDefault()?.Score;
        review.MatchEvidenceJson = JsonSerializer.Serialize(matches);
        review.Status = "Pending";
        review.ResolvedAt = null;
        review.DeferredUntil = null;
        if (prior is null) db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync(ct);
        return review;
    }

    private CatalogueNameStats? _nameStats;

    /// <summary>
    /// Catalogue name usage, loaded once per service instance. Rows added later in the
    /// same import are not counted, which only makes a shared alias look slightly rarer.
    /// </summary>
    private async Task<CatalogueNameStats> NameUsageAsync(CancellationToken ct)
        => _nameStats ??= await CatalogueNameUsage.LoadAsync(db, ct);

    /// <summary>
    /// Loads genres and themes for the few candidates about to be scored. The import's
    /// catalogue scan loads only names, since loading tags for every row would be costly.
    /// </summary>
    private async Task LoadTagsAsync(IEnumerable<Anime> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates.DistinctBy(candidate => candidate.Id))
        {
            var entry = db.Entry(candidate);
            if (entry.State == EntityState.Detached) continue;
            if (!entry.Collection(item => item.AnimeGenres).IsLoaded) await entry.Collection(item => item.AnimeGenres).Query().Include(item => item.Genre).LoadAsync(ct);
            if (!entry.Collection(item => item.AnimeThemes).IsLoaded) await entry.Collection(item => item.AnimeThemes).Query().Include(item => item.Theme).LoadAsync(ct);
        }
    }

    private static List<CandidateMatch> ScoreCandidates(Anime source, IEnumerable<Anime> candidates, MatchScoring scoring, CatalogueNameStats nameStats)
        => candidates.DistinctBy(candidate => candidate.Id)
            .Select(candidate => MatchScorer.Score(source, candidate, scoring, nameStats))
            .OrderByDescending(match => match.Score)
            .ToList();

    /// <summary>
    /// Fills one catalogue row's missing AniList or MAL ID from AniList. An ID that
    /// already belongs to another row is reported rather than written, since the two
    /// rows are then likely duplicates that need a person to reconcile.
    /// </summary>
    public async Task<ProviderIdBackfillResult> BackfillProviderIdsAsync(int animeId, CancellationToken ct = default)
    {
        var anime = await db.Animes.FirstOrDefaultAsync(item => item.Id == animeId, ct);
        if (anime is null || (anime.AniListId is > 0) == (anime.MALId is > 0)) return new(ProviderIdBackfillOutcome.Skipped);
        var ids = await aniList.GetProviderIdsAsync(anime.AniListId, anime.MALId, ct);
        var aniListId = anime.AniListId is > 0 ? null : ids?.AniListId;
        var malId = anime.MALId is > 0 ? null : ids?.MalId;
        if (aniListId is not > 0 && malId is not > 0) return new(ProviderIdBackfillOutcome.NotFound);

        var others = db.Animes.Where(other => other.Id != anime.Id);
        var duplicateId = aniListId is > 0
            ? await others.Where(other => other.AniListId == aniListId).Select(other => (int?)other.Id).FirstOrDefaultAsync(ct)
            : await others.Where(other => other.MALId == malId).Select(other => (int?)other.Id).FirstOrDefaultAsync(ct);
        if (duplicateId is int duplicate) return new(ProviderIdBackfillOutcome.Duplicate, duplicate);

        if (aniListId is > 0) anime.AniListId = aniListId;
        if (malId is > 0) anime.MALId = malId;
        await db.SaveChangesAsync(ct);
        return new(ProviderIdBackfillOutcome.Filled);
    }

    /// <summary>
    /// Re-applies provider-ID bridging to a pending review. A shared ID merges it,
    /// IDs or relations ruling out every candidate create it, and otherwise the
    /// review keeps only its viable candidates with refreshed scores.
    /// </summary>
    public async Task<PendingReviewRecheckOutcome> RecheckPendingReviewAsync(int reviewId, CancellationToken ct = default)
    {
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.Status == "Pending", ct);
        var snapshot = review is null ? null : JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson);
        if (review is null || snapshot is null) return PendingReviewRecheckOutcome.Unchanged;
        var source = FromSnapshot(snapshot);
        var candidateIds = JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [];
        var candidates = await db.Animes.Include(item => item.TitleAliases).Where(item => candidateIds.Contains(item.Id)).ToListAsync(ct);

        await CompleteProviderIdsAsync(source, candidates, ct);
        candidates = WithoutRejected(await DecisionsForAsync(source.AniListId, source.MALId, ct), source, candidates);
        snapshot.AniListId = source.AniListId;
        snapshot.MALId = source.MALId;
        review.PayloadJson = JsonSerializer.Serialize(snapshot);

        var confirmedId = source.AniListId is > 0
            ? await db.Animes.Where(item => item.AniListId == source.AniListId).Select(item => (int?)item.Id).FirstOrDefaultAsync(ct)
            : null;
        if (confirmedId is null && source.MALId is > 0)
            confirmedId = await db.Animes.Where(item => item.MALId == source.MALId).Select(item => (int?)item.Id).FirstOrDefaultAsync(ct);
        if (confirmedId is int confirmed && !review.AutoDecisionUndone)
        {
            // The confirmed row may not have been a title candidate.
            if (!candidateIds.Contains(confirmed)) review.CandidateAnimeIdsJson = JsonSerializer.Serialize(candidateIds.Append(confirmed));
            await db.SaveChangesAsync(ct);
            return await ResolveAutomaticallyAsync(review, "Merge", confirmed, "provider IDs matched", ct)
                ? PendingReviewRecheckOutcome.Merged : PendingReviewRecheckOutcome.Unchanged;
        }

        var viable = ViableCandidates(source, candidates);
        if (viable.Count == 0 && !review.AutoDecisionUndone)
        {
            await db.SaveChangesAsync(ct);
            return await ResolveAutomaticallyAsync(review, "Create", null, "provider IDs or relations ruled out every candidate", ct)
                ? PendingReviewRecheckOutcome.Created : PendingReviewRecheckOutcome.Unchanged;
        }

        if (viable.Count > 0)
        {
            var settings = await MatchingSettings.LoadAsync(db, ct);
            await LoadTagsAsync(viable, ct);
            var matches = ScoreCandidates(source, viable, settings.Scoring, await NameUsageAsync(ct));
            review.CandidateAnimeIdsJson = JsonSerializer.Serialize(viable.Select(candidate => candidate.Id));
            review.MatchScore = matches[0].Score;
            review.MatchEvidenceJson = JsonSerializer.Serialize(matches);
        }
        await db.SaveChangesAsync(ct);
        var applied = await ApplySuggestionAsync(review.Id, await MatchingSettings.LoadAsync(db, ct), ct);
        if (applied is not PendingReviewRecheckOutcome.Unchanged) return applied;
        // With every candidate ruled out on an undone review, the list is left for the person.
        return viable.Count > 0 && viable.Count < candidateIds.Count ? PendingReviewRecheckOutcome.Narrowed : PendingReviewRecheckOutcome.Unchanged;
    }

    /// <summary>
    /// Resolves a pending review by its decision band when automatic decisions are
    /// on. Reviews a person has already overruled are left for that person.
    /// </summary>
    private async Task<PendingReviewRecheckOutcome> ApplySuggestionAsync(int reviewId, MatchingSettings settings, CancellationToken ct)
    {
        if (settings.Mode != AutoDecisionMode.Apply) return PendingReviewRecheckOutcome.Unchanged;
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.Status == "Pending", ct);
        if (review is null || review.AutoDecisionUndone || string.IsNullOrWhiteSpace(review.MatchEvidenceJson)) return PendingReviewRecheckOutcome.Unchanged;
        var matches = JsonSerializer.Deserialize<List<CandidateMatch>>(review.MatchEvidenceJson) ?? [];
        if (MatchScorer.Suggest(matches, settings) is not { } suggestion) return PendingReviewRecheckOutcome.Unchanged;
        if (!await ResolveAutomaticallyAsync(review, suggestion.Decision, suggestion.AnimeId, suggestion.Explanation, ct)) return PendingReviewRecheckOutcome.Unchanged;
        return suggestion.Decision == "Merge" ? PendingReviewRecheckOutcome.Merged : PendingReviewRecheckOutcome.Created;
    }

    /// <summary>
    /// Reverses an automatic Merge or Create and returns the review to the queue,
    /// marked so automation will not decide it again. Returns null on success, or
    /// why the decision cannot be undone.
    /// </summary>
    public async Task<string?> UndoAutomaticResolutionAsync(int reviewId, CancellationToken ct = default)
    {
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.AutoResolved && (item.Status == "Merged" || item.Status == "Created"), ct);
        if (review is null) return "This review is not an automatic decision that can be undone.";
        if (review.ResolvedAnimeId is not int animeId) return "The resolved catalogue record was not recorded.";

        if (review.Status == "Created")
        {
            var created = await db.Animes.FirstOrDefaultAsync(item => item.Id == animeId, ct);
            if (created is not null)
            {
                // Deleting a record someone has since used would lose their work.
                if (await db.Animes.Where(item => item.Id == animeId).SelectMany(item => item.Requests).AnyAsync(ct) ||
                    await db.AnimeCollectionItems.AnyAsync(item => item.AnimeId == animeId, ct))
                    return "The created record has requests or collection entries; remove those first.";
                db.Animes.Remove(created);
            }
        }
        else
        {
            var restore = string.IsNullOrWhiteSpace(review.PreMergeSnapshotJson) ? null : JsonSerializer.Deserialize<AnimeRestorePoint>(review.PreMergeSnapshotJson);
            if (restore is null) return "No pre-merge copy of the record was saved.";
            var target = await db.Animes.Include(item => item.TitleAliases).Include(item => item.Images).Include(item => item.SourceTitles).FirstOrDefaultAsync(item => item.Id == animeId, ct);
            if (target is null) return "The merged catalogue record no longer exists.";
            await RestoreAsync(target, restore, ct);
            var snapshot = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new();
            var (aniListId, malId) = SourceIds(review, snapshot);
            await RememberAsync(aniListId, malId, animeId, isSame: false, "Undo", ct);
        }

        review.Status = "Pending";
        review.ResolvedAt = null;
        review.ResolvedAnimeId = null;
        review.PreMergeSnapshotJson = null;
        review.AutoResolved = false;
        review.AutoDecisionUndone = true;
        review.Reason = $"{review.Reason} · automatic decision undone";
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static AnimeRestorePoint ToRestorePoint(Anime anime)
    {
        var fields = ToSnapshot(anime);
        fields.AlternativeTitles = [];
        return new AnimeRestorePoint
        {
            Fields = fields,
            LocalImagePath = anime.LocalImagePath,
            PreferredImageId = anime.PreferredImageId,
            Aliases = anime.TitleAliases.Select(alias => alias.Title).ToList(),
            ImageIds = anime.Images.Select(image => image.Id).ToList(),
            SourceTitles = anime.SourceTitles.ToDictionary(title => title.Source, title => title.Title),
            NamesReviewed = anime.NamesReviewed,
            NameFromSources = anime.NameFromSources
        };
    }

    private async Task RestoreAsync(Anime target, AnimeRestorePoint restore, CancellationToken ct)
    {
        // Assign directly: Copy() deliberately refuses to clear IDs or downgrade
        // an English title, which is exactly what an undo has to do.
        var fields = restore.Fields;
        target.Title = fields.Title; target.HasEnglishTitle = fields.HasEnglishTitle; target.OriginalTitle = fields.OriginalTitle;
        target.Synopsis = fields.Synopsis; target.ImageUrl = fields.ImageUrl; target.LocalImagePath = restore.LocalImagePath;
        target.AniListId = fields.AniListId; target.MALId = fields.MALId; target.KitsuId = fields.KitsuId;
        target.Type = fields.Type; target.Episodes = fields.Episodes; target.Duration = fields.Duration; target.Rating = fields.Rating;
        target.Status = fields.Status; target.StartDate = fields.StartDate; target.EndDate = fields.EndDate;
        db.AnimeTitleAliases.RemoveRange(target.TitleAliases.Where(alias => !restore.Aliases.Contains(alias.Title, StringComparer.OrdinalIgnoreCase)));
        target.NamesReviewed = restore.NamesReviewed; target.NameFromSources = restore.NameFromSources;
        foreach (var sourceTitle in target.SourceTitles.ToList())
        {
            if (restore.SourceTitles.TryGetValue(sourceTitle.Source, out var title)) sourceTitle.Title = title;
            else db.AnimeSourceTitles.Remove(sourceTitle);
        }
        db.AnimeImages.RemoveRange(target.Images.Where(image => !restore.ImageIds.Contains(image.Id)));
        target.PreferredImageId = restore.PreferredImageId is int preferred && restore.ImageIds.Contains(preferred) ? preferred : null;
        await db.SaveChangesAsync(ct);
        await ReplaceTagsAsync(target, FromSnapshot(fields), ct);
    }

    private async Task<bool> ResolveAutomaticallyAsync(PendingAnimeReview review, string decision, int? candidateId, string because, CancellationToken ct)
    {
        if (!await ResolveCoreAsync(review.Id, decision, candidateId, rememberDecision: false, ct)) return false;
        review.AutoResolved = true;
        review.Reason = $"{review.Reason} · resolved automatically: {because}";
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Fills the provider ID the source lacks from AniList, but only when a candidate
    /// carries that ID type; otherwise the comparison is already decided without it.
    /// A failed lookup leaves the source unchanged so the match falls back to review.
    /// </summary>
    private async Task CompleteProviderIdsAsync(Anime source, IEnumerable<Anime> candidates, CancellationToken ct)
    {
        var missingAniList = source.AniListId is not > 0;
        var missingMal = source.MALId is not > 0;
        if (!candidates.Any(candidate => (missingAniList && candidate.AniListId is > 0) || (missingMal && candidate.MALId is > 0))) return;
        ProviderIds? ids;
        try
        {
            ids = await aniList.GetProviderIdsAsync(source.AniListId, source.MALId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return;
        }
        if (ids is null) return;
        if (missingAniList) source.AniListId = ids.AniListId;
        if (missingMal && ids.MalId is > 0) source.MALId = ids.MalId;
    }

    private static bool TryGetByProviderIds(Dictionary<string, Anime> existing, Anime source, out Anime target)
    {
        if (source.AniListId is > 0 && existing.TryGetValue($"a:{source.AniListId}", out target!)) return true;
        if (source.MALId is > 0 && existing.TryGetValue($"m:{source.MALId}", out target!)) return true;
        target = null!;
        return false;
    }

    /// <summary>Candidates that provider IDs or franchise relations have not ruled out.</summary>
    private static List<Anime> ViableCandidates(Anime source, IEnumerable<Anime> candidates)
        => candidates.Where(candidate => !HasConflictingIds(source, candidate) && !MatchScorer.IsRelated(source, candidate)).ToList();

    private static bool HasConflictingIds(Anime source, Anime candidate)
        => (source.AniListId is > 0 && candidate.AniListId is > 0 && source.AniListId != candidate.AniListId) ||
           HasConflictingMalId(source, candidate) ||
           MatchScorer.HasConflictingKitsuId(source, candidate);

    private static bool HasConflictingMalId(Anime source, Anime candidate)
        => source.MALId is > 0 && candidate.MALId is > 0 && source.MALId != candidate.MALId;

    private static AnimeImportSnapshot ToSnapshot(Anime source) => new()
    {
        AniListId = source.AniListId, MALId = source.MALId, KitsuId = source.KitsuId,
        Title = source.Title, HasEnglishTitle = source.HasEnglishTitle, OriginalTitle = source.OriginalTitle,
        DataSource = source.DataSource, SourceEnglishTitle = source.SourceEnglishTitle,
        AlternativeTitles = source.AlternativeTitles, Synopsis = source.Synopsis, ImageUrl = source.ImageUrl,
        Type = source.Type, Episodes = source.Episodes, Duration = source.Duration, Rating = source.Rating,
        Status = source.Status, StartDate = source.StartDate, EndDate = source.EndDate,
        Genres = source.AnimeGenres.Select(x => x.Genre.Name).ToList(),
        Themes = source.AnimeThemes.Select(x => x.Theme.Name).ToList(),
        Demographics = source.AnimeDemographics.Select(x => x.Demographic.Name).ToList(),
        SourceImages = source.SourceImages, ProviderRelations = source.ProviderRelations
    };

    internal static Anime FromSnapshot(AnimeImportSnapshot source) => new()
    {
        AniListId = source.AniListId, MALId = source.MALId, KitsuId = source.KitsuId,
        Title = source.Title, HasEnglishTitle = source.HasEnglishTitle, OriginalTitle = source.OriginalTitle,
        DataSource = source.DataSource, SourceEnglishTitle = source.SourceEnglishTitle,
        AlternativeTitles = source.AlternativeTitles, Synopsis = source.Synopsis, ImageUrl = source.ImageUrl,
        Type = source.Type, Episodes = source.Episodes, Duration = source.Duration, Rating = source.Rating,
        Status = source.Status, StartDate = source.StartDate, EndDate = source.EndDate, SourceImages = source.SourceImages,
        ProviderRelations = source.ProviderRelations,
        AnimeGenres = source.Genres.Select(name => new AnimeGenre { Genre = new Genre { Name = name } }).ToList(),
        AnimeThemes = source.Themes.Select(name => new AnimeTheme { Theme = new Theme { Name = name } }).ToList(),
        AnimeDemographics = source.Demographics.Select(name => new AnimeDemographic { Demographic = new Demographic { Name = name } }).ToList()
    };

    /// <summary>
    /// Exact and fuzzy title lookup over the catalogue for one import batch. Each row's
    /// names are normalized and tokenized once when added, not once per incoming title.
    /// </summary>
    private sealed class CatalogueNameIndex
    {
        // Providers often vary only by articles and season notation, e.g.
        // "The Saga of Tanya the Evil II" vs "Saga of Tanya the Evil Season 2".
        // A fuzzy match needs at least two meaningful tokens and this token overlap.
        private const double FuzzyThreshold = 0.9;

        private readonly Dictionary<string, HashSet<Anime>> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<Anime>> _byToken = new(StringComparer.Ordinal);
        private readonly Dictionary<Anime, List<HashSet<string>>> _tokenSets = new(ReferenceEqualityComparer.Instance);

        /// <summary>Indexes the source's names under the row they resolved to (the source itself by default).</summary>
        public void Add(Anime source, Anime? target = null)
        {
            var resolved = target ?? source;
            foreach (var name in Names(source))
            {
                if (!_byName.TryGetValue(name, out var matches)) _byName[name] = matches = [];
                matches.Add(resolved);
            }
            if (!_tokenSets.TryGetValue(resolved, out var sets)) _tokenSets[resolved] = sets = [];
            foreach (var tokens in TokenSets(source))
            {
                if (sets.Any(known => known.SetEquals(tokens))) continue;
                sets.Add(tokens);
                foreach (var token in tokens)
                {
                    if (!_byToken.TryGetValue(token, out var rows)) _byToken[token] = rows = [];
                    rows.Add(resolved);
                }
            }
        }

        /// <summary>
        /// Rows sharing an exact name with the source, then rows whose names are a close
        /// token match. Both always run, so a wrong exact match cannot hide the right row.
        /// </summary>
        public List<Anime> FindCandidates(Anime source)
        {
            var exact = Names(source).Where(_byName.ContainsKey).SelectMany(name => _byName[name]);
            var sourceSets = TokenSets(source).ToList();
            var fuzzy = sourceSets.SelectMany(PossibleMatches).Distinct()
                .Select(row => (Row: row, Score: BestScore(sourceSets, _tokenSets[row])))
                .Where(match => match.Score >= FuzzyThreshold)
                .OrderByDescending(match => match.Score)
                .Select(match => match.Row);
            return exact.Concat(fuzzy).Where(candidate => !HasConflictingMalId(source, candidate)).Distinct().ToList();
        }

        // Prefix filter: a row reaching the threshold shares at least minShared of the
        // source's n tokens, so it holds one of any (n - minShared + 1) of them. Checking
        // only the rarest ones keeps common words such as "season" from scanning everything.
        private IEnumerable<Anime> PossibleMatches(HashSet<string> sourceTokens)
        {
            var minShared = (int)Math.Ceiling(FuzzyThreshold * sourceTokens.Count - 1e-9);
            return sourceTokens
                .Select(token => _byToken.TryGetValue(token, out var rows) ? rows : [])
                .OrderBy(rows => rows.Count)
                .Take(sourceTokens.Count - minShared + 1)
                .SelectMany(rows => rows);
        }

        private static double BestScore(List<HashSet<string>> left, List<HashSet<string>> right)
            => left.SelectMany(l => right.Select(r => TitleNormalizer.TokenScore(l, r))).DefaultIfEmpty(0).Max();

        // Tokenize the original titles rather than Names(), which removes word
        // boundaries for exact comparisons and would collapse tokens before scoring.
        private static IEnumerable<HashSet<string>> TokenSets(Anime anime)
            => RawNames(anime).Select(TitleNormalizer.Tokens).Where(tokens => tokens.Count >= 2);
    }

    // Each name is indexed exactly, with romaji long vowels folded ("~"), and with season
    // labels in one form ("#"); the prefixes keep the forms from colliding. "Yuuki" finds a
    // row titled "Yūki", and "Frieren 2nd Season" finds one titled "Frieren Season 2".
    private static IEnumerable<string> Names(Anime anime)
        => RawNames(anime)
            .Where(title => TitleNormalizer.Compact(title).Length >= 3)
            .SelectMany(title => new[]
            {
                TitleNormalizer.Compact(title),
                "~" + TitleNormalizer.RomajiKey(title),
                "#" + TitleNormalizer.SeasonCanonical(title).Replace(" ", string.Empty, StringComparison.Ordinal)
            })
            .Distinct();

    private static IEnumerable<string> RawNames(Anime anime)
        => new[] { anime.Title, anime.OriginalTitle }.Concat(anime.AlternativeTitles).Concat(anime.TitleAliases.Select(alias => alias.Title))
            .OfType<string>().Where(title => !string.IsNullOrWhiteSpace(title));

    private static void Copy(Anime t, Anime s)
    {
        // Prefer an explicitly localized English title and never replace it with
        // a provider's romaji/default title on a later catalogue sync. Once a name
        // has been reviewed (by a person or the English-name pre-check) a sync leaves
        // it alone; the source's names are still stored as aliases and source titles.
        if (!t.NamesReviewed && (!t.HasEnglishTitle || s.HasEnglishTitle))
        {
            t.Title = s.Title;
            t.HasEnglishTitle = s.HasEnglishTitle;
        }
        t.OriginalTitle=s.OriginalTitle; t.Synopsis=s.Synopsis; t.ImageUrl=s.ImageUrl;
        if (s.MALId is > 0 && (t.MALId is null or 0 || t.MALId == s.MALId)) t.MALId=s.MALId;
        if (s.AniListId is > 0 && (t.AniListId is null or 0 || t.AniListId == s.AniListId)) t.AniListId=s.AniListId;
        if (s.KitsuId is > 0 && (t.KitsuId is null or 0 || t.KitsuId == s.KitsuId)) t.KitsuId=s.KitsuId;
        t.Type=s.Type; t.Episodes=s.Episodes; t.Duration=s.Duration; t.Rating=s.Rating; t.Status=s.Status; t.StartDate=s.StartDate; t.EndDate=s.EndDate;
    }
    private async Task StoreTitleAliasesAsync(Anime target, Anime source, CancellationToken ct)
    {
        var titles = new[] { source.Title, source.OriginalTitle }.Concat(source.AlternativeTitles)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var existing = (await db.AnimeTitleAliases
            .Where(alias => alias.AnimeId == target.Id)
            .Select(alias => alias.Title)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A single provider page can contain multiple entries that resolve to the
        // same local anime. Those aliases are not visible to a database query until
        // the batch is saved, so include newly tracked aliases before adding more.
        foreach (var pending in db.ChangeTracker.Entries<AnimeTitleAlias>()
            .Where(entry => entry.State != EntityState.Deleted &&
                            (entry.Entity.AnimeId == target.Id || ReferenceEquals(entry.Entity.Anime, target)))
            .Select(entry => entry.Entity.Title))
            existing.Add(pending);

        foreach (var title in titles.Where(title => existing.Add(title)))
            db.AnimeTitleAliases.Add(new AnimeTitleAlias { Anime = target, Title = title });
        await SourceTitleStore.RecordAsync(db, target, source, ct);
    }
    private async Task StoreImagesAsync(Anime target, Anime source, CancellationToken ct)
    {
        var candidates = (source.SourceImages.Count > 0
            ? source.SourceImages
            : string.IsNullOrWhiteSpace(source.ImageUrl) ? [] : [new SourceImage { Source = "Unknown", Type = "Poster", Url = source.ImageUrl }])
            .Where(image => !string.IsNullOrWhiteSpace(image.Url))
            .GroupBy(image => (image.Source, image.Type, image.Url)).Select(group => group.First()).ToList();
        if (candidates.Count == 0) return;

        var existing = (await db.AnimeImages.Where(image => image.AnimeId == target.Id).ToListAsync(ct))
            .ToDictionary(image => (image.Source, image.Type, image.ImageUrl));
        var images = new List<AnimeImage>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!existing.TryGetValue((candidate.Source, candidate.Type, candidate.Url), out var image))
            {
                image = new AnimeImage { AnimeId = target.Id, Source = candidate.Source, Type = candidate.Type, ImageUrl = candidate.Url };
                db.AnimeImages.Add(image);
            }
            images.Add(image);
        }
        await db.SaveChangesAsync(ct);
        foreach (var image in images)
        {
            var candidate = candidates.First(item => item.Source == image.Source && item.Type == image.Type && item.Url == image.ImageUrl);
            image.LocalImagePath ??= await imageCache.CacheArtworkAsync(candidate.Url, target.Id, candidate.Source, candidate.Type);
        }
    }
    private async Task ReplaceTagsAsync(Anime target, Anime source, CancellationToken ct)
    {
        await db.AnimeGenres.Where(x => x.AnimeId == target.Id).ExecuteDeleteAsync(ct);
        await db.AnimeThemes.Where(x => x.AnimeId == target.Id).ExecuteDeleteAsync(ct);
        await db.AnimeDemographics.Where(x => x.AnimeId == target.Id).ExecuteDeleteAsync(ct);

        var genreNames = TagNames(source.AnimeGenres.Select(x => x.Genre.Name));
        var themeNames = TagNames(source.AnimeThemes.Select(x => x.Theme.Name));
        var demographicNames = TagNames(source.AnimeDemographics.Select(x => x.Demographic.Name));
        var genres = await TagsByNameAsync(db.Genres, genreNames, ct);
        var themes = await TagsByNameAsync(db.Themes, themeNames, ct);
        var demographics = await TagsByNameAsync(db.Demographics, demographicNames, ct);

        foreach (var name in genreNames) { if (!genres.TryGetValue(name, out var tag)) { tag = new Genre { Name = name }; db.Genres.Add(tag); genres[name] = tag; } db.AnimeGenres.Add(new() { AnimeId = target.Id, Genre = tag }); }
        foreach (var name in themeNames) { if (!themes.TryGetValue(name, out var tag)) { tag = new Theme { Name = name }; db.Themes.Add(tag); themes[name] = tag; } db.AnimeThemes.Add(new() { AnimeId = target.Id, Theme = tag }); }
        foreach (var name in demographicNames) { if (!demographics.TryGetValue(name, out var tag)) { tag = new Demographic { Name = name }; db.Demographics.Add(tag); demographics[name] = tag; } db.AnimeDemographics.Add(new() { AnimeId = target.Id, Demographic = tag }); }
        await db.SaveChangesAsync(ct);
        InvalidateLookupCaches();
    }

    private static List<string> TagNames(IEnumerable<string?> names) => names.Select(NormalizeTagName)
        .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task<Dictionary<string, TTag>> TagsByNameAsync<TTag>(DbSet<TTag> tags, List<string> names, CancellationToken ct) where TTag : class
    {
        var nameProperty = typeof(TTag).GetProperty("Name")!;
        var matches = await tags.Where(tag => names.Contains(EF.Property<string>(tag, "Name"))).ToListAsync(ct);
        return matches.ToDictionary(tag => (string)nameProperty.GetValue(tag)!, StringComparer.OrdinalIgnoreCase);
    }
    private static string NormalizeTagName(string? name) => name?.Trim() ?? string.Empty;
    private async Task<Season> GetOrCreateSeasonAsync(string name, int year, CancellationToken ct) { var season=await db.Seasons.FirstOrDefaultAsync(x=>x.Name==name&&x.Year==year,ct); if(season != null) return season; season=new Season{Name=name,Year=year}; db.Seasons.Add(season); await db.SaveChangesAsync(ct); return season; }
    private async Task<int> UnknownSeasonIdAsync(CancellationToken ct) => (await GetOrCreateSeasonAsync("unknown", 0, ct)).Id;
    private static string SeasonName(DateTime? date) => (date?.Month ?? DateTime.UtcNow.Month) switch { <=3=>"winter",<=6=>"spring",<=9=>"summer",_=>"fall" };
}

public enum ProviderIdBackfillOutcome { Filled, NotFound, Duplicate, Skipped }

public sealed record ProviderIdBackfillResult(ProviderIdBackfillOutcome Outcome, int? DuplicateAnimeId = null);

public enum PendingReviewRecheckOutcome { Unchanged, Narrowed, Merged, Created }
