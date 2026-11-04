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

    public async Task<List<PendingAnimeReviewItem>> GetPendingAsync()
    {
        var reviews = await db.PendingAnimeReviews
            .Where(review => review.Status == "Pending")
            .OrderByDescending(review => review.CreatedAt)
            .ToListAsync();
        var ids = reviews.SelectMany(review => JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? []).Distinct().ToList();
        var anime = await db.Animes.Include(item => item.Season).Include(item => item.TitleAliases).Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id);
        var settings = await MatchingSettings.LoadAsync(db);
        return reviews.Select(review =>
        {
            var incoming = JsonSerializer.Deserialize<AnimeImportSnapshot>(review.PayloadJson) ?? new();
            var candidates = (JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [])
                .Where(anime.ContainsKey).Select(id => anime[id]).ToList();
            var matches = MatchesFor(review, incoming, candidates);
            return new PendingAnimeReviewItem
            {
                Review = review,
                Incoming = incoming,
                Candidates = candidates.OrderByDescending(candidate => matches.GetValueOrDefault(candidate.Id)?.Score ?? 0).ToList(),
                Matches = matches,
                // Undone reviews belong to a person; automation stops suggesting for them.
                Suggestion = settings.Mode == AutoDecisionMode.Off || review.AutoDecisionUndone
                    ? null
                    : MatchScorer.Suggest(candidates.Where(candidate => matches.ContainsKey(candidate.Id)).Select(candidate => matches[candidate.Id]).ToList(), settings.MergeThreshold)
            };
        })
            // Highest-confidence decisions first; reviews without a live candidate last.
            .OrderByDescending(item => item.BestMatch?.Score ?? -1)
            .ThenByDescending(item => item.Review.CreatedAt)
            .ToList();
    }

    private static Dictionary<int, CandidateMatch> MatchesFor(PendingAnimeReview review, AnimeImportSnapshot incoming, List<Anime> candidates)
    {
        var stored = string.IsNullOrWhiteSpace(review.MatchEvidenceJson)
            ? []
            : JsonSerializer.Deserialize<List<CandidateMatch>>(review.MatchEvidenceJson) ?? [];
        var matches = stored.ToDictionary(match => match.AnimeId);
        // Reviews queued before scoring existed have no stored evidence. Score
        // them for display without changing the stored review.
        if (candidates.Any(candidate => !matches.ContainsKey(candidate.Id)))
        {
            var source = AnimeCacheService.FromSnapshot(incoming);
            foreach (var candidate in candidates.Where(candidate => !matches.ContainsKey(candidate.Id)))
                matches[candidate.Id] = MatchScorer.Score(source, candidate);
        }
        return matches;
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

    public async Task<bool> SetMainEnglishTitleAsync(int animeId, string title, bool allowCustom = false)
    {
        var anime = await db.Animes.Include(item => item.TitleAliases).FirstOrDefaultAsync(item => item.Id == animeId);
        if (anime is null || string.IsNullOrWhiteSpace(title)) return false;
        title = title.Trim();
        var isHeld = string.Equals(anime.Title, title, StringComparison.OrdinalIgnoreCase)
            || string.Equals(anime.OriginalTitle, title, StringComparison.OrdinalIgnoreCase)
            || anime.TitleAliases.Any(alias => string.Equals(alias.Title, title, StringComparison.OrdinalIgnoreCase));
        if (!isHeld && !allowCustom) return false;
        if (!string.Equals(anime.Title, title, StringComparison.OrdinalIgnoreCase) &&
            !anime.TitleAliases.Any(alias => string.Equals(alias.Title, anime.Title, StringComparison.OrdinalIgnoreCase)))
            db.AnimeTitleAliases.Add(new AnimeTitleAlias { AnimeId = anime.Id, Title = anime.Title });
        anime.Title = title;
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
