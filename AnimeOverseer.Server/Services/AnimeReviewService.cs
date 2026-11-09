using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

public sealed class AnimeReviewService(AnimeDbContext db, AnimeCacheService cache)
{
    public Task<int> GetPendingCountAsync() =>
        db.PendingAnimeReviews.CountAsync(review => review.Status == "Pending");

    public async Task<List<AnimeNameReviewItem>> GetNamesPendingReviewAsync()
    {
        var anime = await db.Animes
            .Include(item => item.Season)
            .Include(item => item.Images)
            .Include(item => item.TitleAliases)
            .Where(item => !item.NamesReviewed)
            .OrderBy(item => item.Title)
            .ToListAsync();
        return anime.Select(item => new AnimeNameReviewItem
        {
            Anime = item,
            Names = new[] { item.OriginalTitle }
                .Concat(item.TitleAliases.Select(alias => alias.Title))
                .Where(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, item.Title, StringComparison.OrdinalIgnoreCase))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        }).Where(item => item.Names.Count > 0).ToList();
    }

    public async Task<int> GetPendingNameCountAsync() =>
        (await GetNamesPendingReviewAsync()).Sum(item => item.Names.Count);

    /// <summary>
    /// The pending queue scored with the saved settings, or with <paramref name="preview"/>
    /// to see what unsaved settings would do. Scores are always recomputed so a
    /// change to the weights shows immediately.
    /// </summary>
    public async Task<List<PendingAnimeReviewItem>> GetPendingAsync(MatchingSettings? preview = null)
    {
        var reviews = await db.PendingAnimeReviews
            .Where(review => review.Status == "Pending")
            .OrderByDescending(review => review.CreatedAt)
            .ToListAsync();
        var ids = reviews.SelectMany(review => JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? []).Distinct().ToList();
        var anime = await db.Animes.Include(item => item.Season).Include(item => item.TitleAliases).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        var settings = preview?.Normalized() ?? await MatchingSettings.LoadAsync(db);
        var nameStats = await CatalogueNameUsage.LoadAsync(db);
        return reviews.Select(review =>
        {
            var incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new();
            var candidates = (JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [])
                .Where(anime.ContainsKey).Select(id => anime[id]).ToList();
            var matches = MatchesFor(incoming, candidates, settings.Scoring, nameStats);
            return new PendingAnimeReviewItem
            {
                Review = review,
                Incoming = incoming,
                Candidates = candidates.OrderByDescending(candidate => matches.GetValueOrDefault(candidate.Id)?.Score ?? 0).ToList(),
                Matches = matches,
                // Undone reviews belong to a person; automation stops suggesting for them.
                Suggestion = settings.Mode == AutoDecisionMode.Off || review.AutoDecisionUndone
                    ? null
                    : MatchScorer.Suggest(candidates.Where(candidate => matches.ContainsKey(candidate.Id)).Select(candidate => matches[candidate.Id]).ToList(), settings)
            };
        })
            // Highest-confidence decisions first; reviews without a live candidate last.
            .OrderByDescending(item => item.BestMatch?.Score ?? -1)
            .ThenByDescending(item => item.Review.CreatedAt)
            .ToList();
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
        var anime = await db.Animes.AsNoTracking().Include(item => item.TitleAliases).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
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

    public async Task<List<AutoResolvedReviewItem>> GetAutoResolvedAsync(int take = 100)
    {
        var reviews = await db.PendingAnimeReviews
            .Where(review => review.AutoResolved && (review.Status == "Merged" || review.Status == "Created"))
            .OrderByDescending(review => review.ResolvedAt)
            .Take(take)
            .ToListAsync();
        var ids = reviews.Where(review => review.ResolvedAnimeId is not null).Select(review => review.ResolvedAnimeId!.Value).Distinct().ToList();
        var anime = await db.Animes.Include(item => item.Season).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        return reviews.Select(review => new AutoResolvedReviewItem
        {
            Review = review,
            Incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new(),
            Resolved = review.ResolvedAnimeId is int id ? anime.GetValueOrDefault(id) : null
        }).ToList();
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

    public async Task<List<RememberedDecisionItem>> GetRememberedDecisionsAsync(int take = 200)
    {
        var decisions = await db.AnimeMatchDecisions.OrderByDescending(decision => decision.DecidedAt).Take(take).ToListAsync();
        var ids = decisions.Select(decision => decision.AnimeId).Distinct().ToList();
        var titles = await db.Animes.Where(anime => ids.Contains(anime.Id)).ToDictionaryAsync(anime => anime.Id, anime => anime.Title);
        // The source's own title lives only in the review that produced the decision.
        var aniListIds = decisions.Where(decision => decision.SourceAniListId is not null).Select(decision => decision.SourceAniListId).Distinct().ToList();
        var malIds = decisions.Where(decision => decision.SourceMalId is not null).Select(decision => decision.SourceMalId).Distinct().ToList();
        var sourceTitles = (await db.PendingAnimeReviews.Where(review => aniListIds.Contains(review.SourceAniListId) || malIds.Contains(review.SourceMalId))
                .Select(review => new { review.SourceAniListId, review.SourceMalId, review.PayloadJson }).ToListAsync())
            .Select(review => (review.SourceAniListId, review.SourceMalId, Title: JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson)?.Title))
            .ToList();
        return decisions.Select(decision => new RememberedDecisionItem
        {
            Decision = decision,
            AnimeTitle = titles.GetValueOrDefault(decision.AnimeId) ?? $"#{decision.AnimeId}",
            SourceTitle = sourceTitles.FirstOrDefault(source => decision.AppliesTo(source.SourceAniListId, source.SourceMalId)).Title
        }).ToList();
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
        review.PayloadJson = JsonSerializer.Serialize(snapshot);
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
