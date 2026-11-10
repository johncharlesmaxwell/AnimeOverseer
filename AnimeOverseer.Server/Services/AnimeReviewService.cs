using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

public sealed class AnimeReviewService(AnimeDbContext db, AnimeCacheService cache)
{
    public Task<int> GetPendingCountAsync() =>
        db.PendingAnimeReviews.CountAsync(review => review.Status == "Pending");

    /// <summary>
    /// Every record waiting for name review, with its name choices. Only the names are
    /// loaded: <see cref="AnimeNameReviewItem.Anime"/> is a lightweight copy without its
    /// season or images, so the whole list stays cheap. Use
    /// <see cref="LoadNameReviewPageAsync"/> for the records a page actually shows.
    /// </summary>
    public async Task<List<AnimeNameReviewItem>> GetNamesPendingReviewAsync() =>
        await NameReviewItemsAsync(db.Animes.Where(item => !item.NamesReviewed).OrderBy(item => item.Title), await EnglishNameSettings.LoadAsync(db));

    /// <summary>Name review items for these records, names only; records with nothing to choose are left out.</summary>
    private async Task<List<AnimeNameReviewItem>> NameReviewItemsAsync(IQueryable<Anime> records, EnglishNameSettings settings)
    {
        var rows = await records.AsNoTracking()
            .Select(item => new
            {
                item.Id, item.Title, item.OriginalTitle, item.StartDate,
                Aliases = item.TitleAliases.Select(alias => alias.Title).ToList(),
                Sources = item.SourceTitles.Select(source => new { source.Source, source.Title }).ToList()
            })
            .AsSplitQuery()
            .ToListAsync();
        return rows.Select(row => NameReviewItem(new Anime
        {
            Id = row.Id, Title = row.Title, OriginalTitle = row.OriginalTitle, StartDate = row.StartDate,
            TitleAliases = row.Aliases.Select(title => new AnimeTitleAlias { AnimeId = row.Id, Title = title }).ToList(),
            SourceTitles = row.Sources.Select(source => new AnimeSourceTitle { AnimeId = row.Id, Source = source.Source, Title = source.Title }).ToList()
        }, settings)).Where(item => item.Names.Count + item.EnglishNames.Count > 0).ToList();
    }

    private static AnimeNameReviewItem NameReviewItem(Anime item, EnglishNameSettings settings)
    {
        var options = EnglishNamePrecheck.Evaluate(item, settings).Options;
        var currentKey = TitleNormalizer.Compact(item.Title);
        var optionKeys = options.Select(option => TitleNormalizer.Compact(option.Title)).ToHashSet(StringComparer.Ordinal);
        bool IsCurrent(string? name) => string.Equals(TitleNormalizer.Compact(name), currentKey, StringComparison.Ordinal);
        return new AnimeNameReviewItem
        {
            Anime = item,
            CurrentSources = options.FirstOrDefault(option => IsCurrent(option.Title))?.Sources ?? [],
            EnglishNames = options.Where(option => !IsCurrent(option.Title)).ToList(),
            Names = new[] { item.OriginalTitle }
                .Concat(item.TitleAliases.Select(alias => alias.Title))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, item.Title, StringComparison.OrdinalIgnoreCase))
                .Select(name => name!.Trim())
                .Where(name => !optionKeys.Contains(TitleNormalizer.Compact(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    /// <summary>
    /// The full records, with season and images, behind one page of name review items. Items
    /// whose record has gone are left out.
    /// </summary>
    public async Task<List<AnimeNameReviewItem>> LoadNameReviewPageAsync(IReadOnlyList<AnimeNameReviewItem> page)
    {
        var ids = page.Select(item => item.Anime.Id).ToList();
        var anime = await db.Animes.AsNoTracking().Include(item => item.Season).Include(item => item.Images)
            .Where(item => ids.Contains(item.Id)).AsSplitQuery().ToDictionaryAsync(item => item.Id);
        return page.Where(item => anime.ContainsKey(item.Anime.Id)).Select(item => new AnimeNameReviewItem
        {
            Anime = anime[item.Anime.Id],
            CurrentSources = item.CurrentSources,
            EnglishNames = item.EnglishNames,
            Names = item.Names
        }).ToList();
    }

    public const int NameCountBatchSize = 250;

    /// <summary>
    /// How many records the name review page lists, counting no further than
    /// <paramref name="stopAt"/>: the Settings badge shows "999+" beyond that, so a long
    /// queue is read a batch at a time and only until the limit is reached. A plain SQL
    /// count would also include records with nothing to choose, which the page never lists.
    /// </summary>
    public async Task<int> GetPendingNameCountAsync(int stopAt = 1000)
    {
        var settings = await EnglishNameSettings.LoadAsync(db);
        var count = 0;
        var afterId = 0;
        while (count < stopAt)
        {
            var batch = await db.Animes.AsNoTracking()
                .Where(item => !item.NamesReviewed && item.Id > afterId)
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .Take(NameCountBatchSize)
                .ToListAsync();
            if (batch.Count == 0) break;
            // An id range rather than the id list keeps each batch's query simple.
            var (fromId, toId) = (afterId, batch[^1]);
            afterId = toId;
            count += (await NameReviewItemsAsync(db.Animes.Where(item => !item.NamesReviewed && item.Id > fromId && item.Id <= toId), settings)).Count;
        }
        return Math.Min(count, stopAt);
    }

    /// <summary>
    /// Names each record whose sources settle its English name, and takes it out of name
    /// review. Records a person has named are never touched; records the pre-check named
    /// earlier are rechecked, and go back to review if their sources no longer settle it.
    /// </summary>
    public async Task<EnglishNamePrecheckResult> ApplyEnglishNamePrecheckAsync(EnglishNameSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= await EnglishNameSettings.LoadAsync(db, ct);
        var anime = await db.Animes
            .Include(item => item.TitleAliases)
            .Include(item => item.SourceTitles)
            .Where(item => (!item.NamesReviewed || item.NameFromSources) && item.SourceTitles.Any())
            .AsSplitQuery()
            .ToListAsync(ct);
        int renamed = 0, confirmed = 0, returned = 0;
        foreach (var item in anime)
        {
            var verdict = EnglishNamePrecheck.Evaluate(item, settings);
            if (!verdict.Applies)
            {
                if (item.NameFromSources)
                {
                    item.NamesReviewed = false;
                    item.NameFromSources = false;
                    returned++;
                }
                continue;
            }
            var title = verdict.Title!;
            if (string.Equals(item.Title, title, StringComparison.Ordinal))
            {
                if (item.NamesReviewed) continue;
                confirmed++;
            }
            else
            {
                KeepAsAlias(item, item.Title);
                item.Title = title;
                renamed++;
            }
            item.HasEnglishTitle = true;
            item.NamesReviewed = true;
            item.NameFromSources = true;
        }
        await db.SaveChangesAsync(ct);
        return new(renamed, confirmed, returned);
    }

    public Task<EnglishNameSettings> GetEnglishNameSettingsAsync() => EnglishNameSettings.LoadAsync(db);

    public Task SaveEnglishNameSettingsAsync(EnglishNameSettings settings) => EnglishNameSettings.SaveAsync(db, settings);

    private void KeepAsAlias(Anime anime, string? title)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            anime.TitleAliases.Any(alias => string.Equals(alias.Title, title, StringComparison.OrdinalIgnoreCase))) return;
        var alias = new AnimeTitleAlias { AnimeId = anime.Id, Title = title };
        anime.TitleAliases.Add(alias);
        db.AnimeTitleAliases.Add(alias);
    }

    /// <summary>
    /// The pending queue scored with the saved settings, or with <paramref name="preview"/>
    /// to see what unsaved settings would do. Scores are always recomputed so a
    /// change to the weights shows immediately.
    /// </summary>
    public async Task<List<PendingAnimeReviewItem>> GetPendingAsync(MatchingSettings? preview = null)
    {
        var settings = preview?.Normalized() ?? await MatchingSettings.LoadAsync(db);
        _nameStats = await CatalogueNameUsage.LoadAsync(db);
        return InQueueOrder(await ScorePendingAsync(db.PendingAnimeReviews.Where(review => review.Status == "Pending"), settings, _nameStats)).ToList();
    }

    // Catalogue-wide name frequencies from the last full load. Building them is most of the
    // cost of scoring the queue, so re-scoring a few reviews reuses them; names added since
    // (by a merge, say) are not counted, which only makes them look slightly rarer.
    private CatalogueNameStats? _nameStats;

    /// <summary>
    /// Scores just these reviews again with the saved settings, for after an action that can
    /// only have changed them. Returns those still pending; resolved or missing ones are left out.
    /// </summary>
    public async Task<List<PendingAnimeReviewItem>> RescorePendingAsync(IReadOnlyCollection<int> reviewIds)
    {
        if (reviewIds.Count == 0) return [];
        var settings = await MatchingSettings.LoadAsync(db);
        _nameStats ??= await CatalogueNameUsage.LoadAsync(db);
        return await ScorePendingAsync(db.PendingAnimeReviews.Where(review => review.Status == "Pending" && reviewIds.Contains(review.Id)), settings, _nameStats);
    }

    /// <summary>The queue's order: highest-confidence decisions first, reviews without a live candidate last.</summary>
    public static IEnumerable<PendingAnimeReviewItem> InQueueOrder(IEnumerable<PendingAnimeReviewItem> items) =>
        items.OrderByDescending(item => item.BestMatch?.Score ?? -1).ThenByDescending(item => item.Review.CreatedAt);

    private async Task<List<PendingAnimeReviewItem>> ScorePendingAsync(IQueryable<PendingAnimeReview> query, MatchingSettings settings, CatalogueNameStats nameStats)
    {
        var reviews = await query.OrderByDescending(review => review.CreatedAt).ToListAsync();
        var ids = reviews.SelectMany(review => JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? []).Distinct().ToList();
        var anime = await db.Animes.Include(item => item.Season).Include(item => item.TitleAliases).Include(item => item.AnimeGenres).ThenInclude(item => item.Genre).Include(item => item.AnimeThemes).ThenInclude(item => item.Theme).AsSplitQuery()
            .Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        return reviews.Select(review =>
        {
            var incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new();
            var candidates = (JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [])
                .Where(anime.ContainsKey).Select(id => anime[id]).ToList();
            var matches = MatchesFor(incoming, candidates, settings.Scoring, nameStats);
            // Undone reviews belong to a person; automation stops suggesting for them.
            var automated = settings.Mode != AutoDecisionMode.Off && !review.AutoDecisionUndone;
            var scored = candidates.Where(candidate => matches.ContainsKey(candidate.Id)).Select(candidate => matches[candidate.Id]).ToList();
            return new PendingAnimeReviewItem
            {
                Review = review,
                Incoming = incoming,
                Candidates = candidates.OrderByDescending(candidate => matches.GetValueOrDefault(candidate.Id)?.Score ?? 0).ToList(),
                Matches = matches,
                Suggestion = automated ? MatchScorer.Suggest(scored, settings) : null,
                Undecided = automated ? MatchScorer.WhyUndecided(scored, settings) : null
            };
        }).ToList();
    }

    // Scored for display without changing the stored review, so the page reflects
    // the current weights. Stored evidence is refreshed by imports and rechecks.
    private static Dictionary<int, CandidateMatch> MatchesFor(AnimeImportSnapshot incoming, List<Anime> candidates, MatchScoring scoring, CatalogueNameStats nameStats)
    {
        var source = AnimeCacheService.FromSnapshot(incoming);
        return candidates.ToDictionary(candidate => candidate.Id, candidate => MatchScorer.Score(source, candidate, scoring, nameStats));
    }

    /// <summary>How the pending queue would be decided under <paramref name="settings"/>.</summary>
    public async Task<MatchingPreview> PreviewAsync(MatchingSettings settings)
    {
        var normalized = settings.Normalized();
        var items = await GetPendingAsync(normalized);
        return new MatchingPreview(items, normalized.Scoring);
    }

    /// <summary>
    /// Replays every review a person merged or created under <paramref name="settings"/>, to show
    /// how often the automatic rules would have agreed. Merge targets are rebuilt from their
    /// pre-merge copy; other candidates are scored as they are now.
    /// </summary>
    public async Task<MatchReplayReport> ReplayDecisionsAsync(MatchingSettings settings)
    {
        var normalized = settings.Normalized();
        var reviews = await db.PendingAnimeReviews.AsNoTracking()
            .Where(review => !review.AutoResolved && (review.Status == "Merged" || review.Status == "Created"))
            .OrderByDescending(review => review.ResolvedAt)
            .ToListAsync();
        var candidateIds = reviews.ToDictionary(review => review.Id, review => JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? []);
        var ids = candidateIds.Values.SelectMany(list => list)
            .Concat(reviews.Where(review => review.ResolvedAnimeId is not null).Select(review => review.ResolvedAnimeId!.Value))
            .Distinct().ToList();
        var anime = await db.Animes.AsNoTracking().Include(item => item.TitleAliases).Include(item => item.AnimeGenres).ThenInclude(item => item.Genre).Include(item => item.AnimeThemes).ThenInclude(item => item.Theme).AsSplitQuery()
            .Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        var stats = await CatalogueNameUsage.LoadAsync(db);

        var cases = new List<MatchReplayCase>();
        var skipped = new Dictionary<string, int>();
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        foreach (var review in reviews)
        {
            var snapshot = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson);
            if (snapshot is null) { Skip("the incoming record could not be read"); continue; }
            var resolved = review.ResolvedAnimeId is int resolvedId ? anime.GetValueOrDefault(resolvedId) : null;
            var merged = review.Status == "Merged";
            List<Anime> candidates;
            HashSet<string> added;
            if (merged)
            {
                if (resolved is null) { Skip("the merged record no longer exists"); continue; }
                var restore = string.IsNullOrWhiteSpace(review.PreMergeSnapshotJson) ? null : JsonSerializer.Deserialize<AnimeRestorePoint>(review.PreMergeSnapshotJson);
                // Without it the target already carries the incoming names and would always look like a match.
                if (restore is null) { Skip("no pre-merge copy of the merged record was saved"); continue; }
                var before = MatchReplay.BeforeMerge(resolved.Id, restore);
                candidates = candidateIds[review.Id].Select(id => id == resolved.Id ? before : anime.GetValueOrDefault(id)).OfType<Anime>().ToList();
                added = MatchReplay.CountedNames(resolved);
                added.ExceptWith(MatchReplay.CountedNames(before));
            }
            else
            {
                candidates = candidateIds[review.Id].Where(id => id != review.ResolvedAnimeId).Select(id => anime.GetValueOrDefault(id)).OfType<Anime>().ToList();
                added = resolved is null ? [] : MatchReplay.CountedNames(resolved);
            }
            var example = new MatchReplayExample(review.Id, AnimeCacheService.FromSnapshot(snapshot), candidates, merged, merged ? resolved!.Id : null, added);
            cases.Add(MatchReplay.Evaluate(example, normalized, stats));
        }
        return new MatchReplayReport(cases, skipped);
    }

    /// <summary>
    /// One page of automatic decisions, searched, filtered and sorted in SQL so the tab never
    /// loads the whole history. Only the page's rows have their payload read.
    /// </summary>
    public async Task<ReviewListPage<AutoResolvedReviewItem>> GetAutoResolvedPageAsync(ReviewListQuery request)
    {
        var query = db.PendingAnimeReviews.AsNoTracking()
            .Where(review => review.AutoResolved && (review.Status == "Merged" || review.Status == "Created"));
        query = request.Show switch
        {
            "merged" => query.Where(review => review.Status == "Merged"),
            "created" => query.Where(review => review.Status == "Created"),
            _ => query
        };
        foreach (var pattern in ReviewListQuery.LikePatterns(request.Search))
            query = query.Where(review =>
                EF.Functions.Like(review.SourceTitle, pattern, ReviewListQuery.LikeEscape) ||
                EF.Functions.Like(review.Reason, pattern, ReviewListQuery.LikeEscape) ||
                db.Animes.Any(anime => anime.Id == review.ResolvedAnimeId && EF.Functions.Like(anime.Title, pattern, ReviewListQuery.LikeEscape)));
        query = request.Sort switch
        {
            "oldest" => query.OrderBy(review => review.ResolvedAt).ThenBy(review => review.Id),
            "title" => query.OrderBy(review => EF.Functions.Collate(review.SourceTitle ?? "", "NOCASE")).ThenBy(review => review.Id),
            _ => query.OrderByDescending(review => review.ResolvedAt).ThenByDescending(review => review.Id)
        };
        var matching = await query.CountAsync();
        var page = ReviewListQuery.ClampPage(request.Page, matching, request.PageSize);
        // Leaves out PreMergeSnapshotJson, a full copy of the merged record kept only for undo.
        var rows = await query.Skip((page - 1) * request.PageSize).Take(request.PageSize)
            .Select(review => new PendingAnimeReview
            {
                Id = review.Id, Status = review.Status, Reason = review.Reason, ResolvedAt = review.ResolvedAt, ResolvedAnimeId = review.ResolvedAnimeId,
                PayloadJson = review.PayloadJson, SourceTitle = review.SourceTitle, SourceAniListId = review.SourceAniListId, SourceMalId = review.SourceMalId,
                AutoResolved = review.AutoResolved, CreatedAt = review.CreatedAt
            })
            .ToListAsync();
        var ids = rows.Where(review => review.ResolvedAnimeId is not null).Select(review => review.ResolvedAnimeId!.Value).Distinct().ToList();
        var anime = await db.Animes.AsNoTracking().Include(item => item.Season).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        var items = rows.Select(review => new AutoResolvedReviewItem
        {
            Review = review,
            Incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new(),
            Resolved = review.ResolvedAnimeId is int id ? anime.GetValueOrDefault(id) : null
        }).ToList();
        return new ReviewListPage<AutoResolvedReviewItem>(items, matching, page);
    }

    public Task<int> GetAutoResolvedCountAsync() =>
        db.PendingAnimeReviews.CountAsync(review => review.AutoResolved && (review.Status == "Merged" || review.Status == "Created"));

    /// <summary>Returns null when undone, otherwise why it could not be.</summary>
    public Task<string?> UndoAutomaticResolutionAsync(int reviewId) => cache.UndoAutomaticResolutionAsync(reviewId);

    public async Task<List<DeferredReviewItem>> GetDeferredAsync()
    {
        var reviews = await db.PendingAnimeReviews.Where(review => review.Status == "Deferred").ToListAsync();
        return reviews
            .OrderBy(review => review.DeferredUntil ?? DateTime.MinValue)
            .Select(review => new DeferredReviewItem { Review = review, Incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new() })
            .ToList();
    }

    public Task<int> GetDeferredCountAsync() => db.PendingAnimeReviews.CountAsync(review => review.Status == "Deferred");

    /// <summary>Ends a snooze early and returns the review to the queue.</summary>
    public async Task<bool> ReopenDeferredAsync(int reviewId)
    {
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.Status == "Deferred");
        if (review is null) return false;
        review.Status = "Pending";
        review.ResolvedAt = null;
        review.DeferredUntil = null;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>One page of remembered decisions, searched, filtered and sorted in SQL.</summary>
    public async Task<ReviewListPage<RememberedDecisionItem>> GetRememberedDecisionsPageAsync(ReviewListQuery request)
    {
        var query = db.AnimeMatchDecisions.AsNoTracking()
            .Select(decision => new { Decision = decision, AnimeTitle = db.Animes.Where(anime => anime.Id == decision.AnimeId).Select(anime => anime.Title).FirstOrDefault() });
        query = request.Show switch
        {
            "same" => query.Where(row => row.Decision.IsSameAnime),
            "different" => query.Where(row => !row.Decision.IsSameAnime),
            _ => query
        };
        var origin = request.Show2 switch { "merge" => "Merge", "create" => "Create", "undo" => "Undo", _ => null };
        if (origin is not null) query = query.Where(row => row.Decision.Origin == origin);
        foreach (var pattern in ReviewListQuery.LikePatterns(request.Search))
            query = query.Where(row =>
                EF.Functions.Like(row.Decision.SourceTitle, pattern, ReviewListQuery.LikeEscape) ||
                EF.Functions.Like(row.AnimeTitle, pattern, ReviewListQuery.LikeEscape));
        query = request.Sort switch
        {
            "oldest" => query.OrderBy(row => row.Decision.DecidedAt).ThenBy(row => row.Decision.Id),
            "title" => query.OrderBy(row => EF.Functions.Collate(row.AnimeTitle ?? "", "NOCASE")).ThenBy(row => row.Decision.Id),
            _ => query.OrderByDescending(row => row.Decision.DecidedAt).ThenByDescending(row => row.Decision.Id)
        };
        var matching = await query.CountAsync();
        var page = ReviewListQuery.ClampPage(request.Page, matching, request.PageSize);
        var rows = await query.Skip((page - 1) * request.PageSize).Take(request.PageSize).ToListAsync();
        var items = rows.Select(row => new RememberedDecisionItem
        {
            Decision = row.Decision,
            AnimeTitle = row.AnimeTitle ?? $"#{row.Decision.AnimeId}",
            SourceTitle = row.Decision.SourceTitle
        }).ToList();
        return new ReviewListPage<RememberedDecisionItem>(items, matching, page);
    }

    public Task<int> GetRememberedDecisionCountAsync() => db.AnimeMatchDecisions.CountAsync();

    public async Task<bool> ForgetDecisionAsync(int decisionId) =>
        await db.AnimeMatchDecisions.Where(decision => decision.Id == decisionId).ExecuteDeleteAsync() > 0;

    public Task<MatchingSettings> GetMatchingSettingsAsync() => MatchingSettings.LoadAsync(db);

    public Task SaveMatchingSettingsAsync(MatchingSettings settings) => MatchingSettings.SaveAsync(db, settings);

    public async Task<int> ResolveAsync(IEnumerable<PendingAnimeReviewItem> items, string decision)
    {
        var resolved = 0;
        foreach (var item in items)
        {
            var candidateId = decision == "Merge" && item.Candidates.Count == 1 ? item.Candidates[0].Id : (int?)null;
            if (await cache.ResolvePendingReviewAsync(item.Review.Id, decision, candidateId)) resolved++;
        }
        return resolved;
    }

    /// <summary>Decides one review; <paramref name="candidateId"/> picks the merge target among its candidates.</summary>
    public Task<bool> ResolveOneAsync(int reviewId, string decision, int? candidateId = null)
        => cache.ResolvePendingReviewAsync(reviewId, decision, decision == "Merge" ? candidateId : null);

    public async Task<bool> SetMainEnglishTitleAsync(int animeId, string title, bool allowCustom = false)
    {
        var anime = await db.Animes.Include(item => item.TitleAliases).FirstOrDefaultAsync(item => item.Id == animeId);
        if (anime is null || string.IsNullOrWhiteSpace(title)) return false;
        title = title.Trim();
        var isHeld = string.Equals(anime.Title, title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(anime.OriginalTitle, title, StringComparison.OrdinalIgnoreCase)
            || anime.TitleAliases.Any(alias => string.Equals(alias.Title, title, StringComparison.OrdinalIgnoreCase));
        if (!isHeld && !allowCustom) return false;
        if (!string.IsNullOrWhiteSpace(anime.Title) &&
            !string.Equals(anime.Title, title, StringComparison.OrdinalIgnoreCase) &&
            !anime.TitleAliases.Any(alias => string.Equals(alias.Title, anime.Title, StringComparison.OrdinalIgnoreCase)))
            db.AnimeTitleAliases.Add(new AnimeTitleAlias { AnimeId = anime.Id, Title = anime.Title });
        anime.Title = title;
        anime.HasEnglishTitle = true;
        anime.NamesReviewed = true;
        anime.NameFromSources = false;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Keeps the current title as the main English name and takes the record out of name review.</summary>
    public async Task<bool> ConfirmCurrentTitleAsync(int animeId)
    {
        var anime = await db.Animes.FirstOrDefaultAsync(item => item.Id == animeId);
        if (anime is null || string.IsNullOrWhiteSpace(anime.Title)) return false;
        anime.HasEnglishTitle = true;
        anime.NamesReviewed = true;
        anime.NameFromSources = false;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetIncomingMainEnglishTitleAsync(int reviewId, string title, bool allowCustom = false)
    {
        var review = await db.PendingAnimeReviews.FirstOrDefaultAsync(item => item.Id == reviewId && item.Status == "Pending");
        if (review is null || string.IsNullOrWhiteSpace(title)) return false;
        var snapshot = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson);
        if (snapshot is null) return false;
        title = title.Trim();
        var isHeld = string.Equals(snapshot.Title, title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.OriginalTitle, title, StringComparison.OrdinalIgnoreCase)
            || snapshot.AlternativeTitles.Any(alias => string.Equals(alias, title, StringComparison.OrdinalIgnoreCase));
        if (!isHeld && !allowCustom) return false;
        if (!string.Equals(snapshot.Title, title, StringComparison.OrdinalIgnoreCase) &&
            !snapshot.AlternativeTitles.Contains(snapshot.Title, StringComparer.OrdinalIgnoreCase))
            snapshot.AlternativeTitles.Add(snapshot.Title);
        snapshot.Title = title;
        snapshot.HasEnglishTitle = true;
        review.SetPayload(snapshot);
        await db.SaveChangesAsync();
        return true;
    }
}

public sealed class PendingAnimeReviewItem
{
    public PendingAnimeReview Review { get; init; } = null!;
    public AnimeImportSnapshot Incoming { get; init; } = new();
    public List<Anime> Candidates { get; init; } = [];
    public Dictionary<int, CandidateMatch> Matches { get; init; } = [];
    public CandidateMatch? BestMatch => Candidates.Count > 0 ? Matches.GetValueOrDefault(Candidates[0].Id) : null;
    public MatchSuggestion? Suggestion { get; init; }
    /// <summary>When automation is on but leaves this review for a person, which rule came closest and why it didn't apply.</summary>
    public string? Undecided { get; init; }
}

public sealed class DeferredReviewItem
{
    public PendingAnimeReview Review { get; init; } = null!;
    public AnimeImportSnapshot Incoming { get; init; } = new();
}

public sealed class RememberedDecisionItem
{
    public AnimeMatchDecision Decision { get; init; } = null!;
    public string AnimeTitle { get; init; } = string.Empty;
    public string? SourceTitle { get; init; }
}

public sealed class AutoResolvedReviewItem
{
    public PendingAnimeReview Review { get; init; } = null!;
    public AnimeImportSnapshot Incoming { get; init; } = new();
    public Anime? Resolved { get; init; }
}

public sealed class AnimeNameReviewItem
{
    public Anime Anime { get; init; } = null!;
    /// <summary>Sources whose English name is the current name.</summary>
    public IReadOnlyList<string> CurrentSources { get; init; } = [];
    /// <summary>Other English names the sources give, most-supported first.</summary>
    public List<EnglishNameOption> EnglishNames { get; init; } = [];
    /// <summary>Every other held name: romaji, native, synonyms.</summary>
    public List<string> Names { get; init; } = [];
}

/// <summary>How the pending queue would be decided under a set of matching settings.</summary>
public sealed class MatchingPreview(List<PendingAnimeReviewItem> items, MatchScoring scoring)
{
    public const string None = "None";

    // Computed once: the page reads these on every render.
    public List<PendingAnimeReviewItem> Items { get; } = items;
    public MatchScoring Scoring { get; } = scoring;
    public int Pending => Items.Count;
    public List<PendingAnimeReviewItem> WouldMerge { get; } = items.Where(item => item.Suggestion?.Decision == "Merge").ToList();
    public List<PendingAnimeReviewItem> WouldCreate { get; } = items.Where(item => item.Suggestion?.Decision == "Create").ToList();
    public List<PendingAnimeReviewItem> NeedPerson { get; } = items.Where(item => item.Suggestion is null).ToList();
    public int Overruled { get; private set; } = items.Count(item => item.Review.AutoDecisionUndone);
    public Dictionary<string, List<PendingAnimeReviewItem>> ByConfidence { get; }
        = items.GroupBy(item => Confidence(item, scoring)).ToDictionary(group => group.Key, group => group.ToList());

    /// <summary>Drops an item that has just been decided, so the counts and lists stay current without re-scoring the queue.</summary>
    public void Remove(PendingAnimeReviewItem item)
    {
        if (!Items.Remove(item)) return;
        WouldMerge.Remove(item);
        WouldCreate.Remove(item);
        NeedPerson.Remove(item);
        if (item.Review.AutoDecisionUndone) Overruled--;
        var band = Confidence(item, Scoring);
        if (ByConfidence.TryGetValue(band, out var group) && group.Remove(item) && group.Count == 0) ByConfidence.Remove(band);
    }

    /// <summary>The confidence level of an item's best candidate, or <see cref="None"/> when it has none.</summary>
    public string ConfidenceOf(PendingAnimeReviewItem item) => Confidence(item, Scoring);

    private static string Confidence(PendingAnimeReviewItem item, MatchScoring scoring)
        => item.BestMatch is { } best ? MatchScorer.Confidence(best, scoring) : None;
}

/// <summary>What a review list page asks for: search words, the filter bar's choices, and the page.</summary>
public sealed record ReviewListQuery(string? Search, string? Show, string? Show2, string? Sort, int Page, int PageSize)
{
    public const string LikeEscape = "\\";

    /// <summary>One case-insensitive LIKE pattern per search word; a row must match every word.</summary>
    public static IEnumerable<string> LikePatterns(string? search) =>
        (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");

    /// <summary>Keeps a requested page inside the matching rows.</summary>
    public static int ClampPage(int page, int matching, int pageSize) =>
        Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(matching / (double)Math.Max(1, pageSize))));
}

/// <summary>One page of a review list, how many rows match the filters, and the page actually returned.</summary>
public sealed record ReviewListPage<T>(List<T> Items, int Matching, int Page);
