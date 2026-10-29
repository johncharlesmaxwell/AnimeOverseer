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
                Matches = matches
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
}

public sealed class AnimeNameReviewItem
{
    public Anime Anime { get; init; } = null!;
    public List<string> Names { get; init; } = [];
}
