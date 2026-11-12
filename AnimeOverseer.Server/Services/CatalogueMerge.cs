using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// Folds one catalogue row into another and deletes it. Everything pointing at the removed row moves to
/// the kept one; the kept row's own values win, and the removed row only fills what the kept row lacks.
/// A merge cannot be undone: the removed row is gone, and splitting requests, collections and decisions
/// back apart could not be done reliably once later imports have touched the kept row. Each merge is
/// recorded in <see cref="AnimeMergeRecord"/> instead.
/// </summary>
public static class CatalogueMerge
{
    public static async Task<CatalogueMergeResult> MergeAsync(AnimeDbContext db, int keepId, int removeId, CancellationToken ct = default)
    {
        if (keepId == removeId) return CatalogueMergeResult.Failed("A record cannot be merged into itself.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var keep = await db.Animes.Include(anime => anime.TitleAliases).Include(anime => anime.Images).Include(anime => anime.SourceTitles)
            .AsSplitQuery().FirstOrDefaultAsync(anime => anime.Id == keepId, ct);
        var remove = await db.Animes.AsNoTracking().Include(anime => anime.TitleAliases).Include(anime => anime.Images).Include(anime => anime.SourceTitles)
            .AsSplitQuery().FirstOrDefaultAsync(anime => anime.Id == removeId, ct);
        if (keep is null || remove is null) return CatalogueMergeResult.Failed("One of the records no longer exists.");

        var moved = new List<string>();
        void Count(int count, string one, string many) { if (count > 0) moved.Add($"{count} {(count == 1 ? one : many)}"); }

        var dropped = FillFields(keep, remove);
        Count(MoveNames(db, keep, remove), "name", "names");
        Count(await MoveImagesAsync(db, keep, remove, ct), "image", "images");
        await MoveSourceTitlesAsync(db, keep, remove, ct);
        await db.SaveChangesAsync(ct);
        await MoveTagsAsync(db, keepId, removeId, ct);

        Count(await db.Requests.Where(request => EF.Property<int?>(request, "AnimeId") == removeId)
            .ExecuteUpdateAsync(set => set.SetProperty(request => EF.Property<int?>(request, "AnimeId"), keepId), ct), "request", "requests");
        Count(await MoveCollectionItemsAsync(db, keepId, removeId, ct), "collection entry", "collection entries");
        await MoveRecommendationsAsync(db, keepId, removeId, ct);
        await MoveRelationsAsync(db, keep, remove, ct);
        Count(await MoveDecisionsAsync(db, keep, removeId, ct), "remembered decision", "remembered decisions");
        Count(await MoveReviewsAsync(db, keepId, removeId, ct), "review", "reviews");

        db.AnimeMergeRecords.Add(new AnimeMergeRecord
        {
            KeptAnimeId = keepId, RemovedAnimeId = removeId, RemovedTitle = remove.Title,
            RemovedAniListId = remove.AniListId, RemovedMalId = remove.MALId, RemovedKitsuId = remove.KitsuId,
            Summary = moved.Count > 0 ? string.Join(" · ", moved) : null
        });
        await db.SaveChangesAsync(ct);
        // Whatever was not moved (duplicate images, aliases, tags, source titles) goes with the row.
        await db.Animes.Where(anime => anime.Id == removeId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return new CatalogueMergeResult(true, null, moved, dropped);
    }

    /// <summary>
    /// Fills the kept row's missing provider IDs and blank fields from the removed row. Returns the removed
    /// row's provider IDs that differ from the kept row's and so were not kept, as "MAL 123".
    /// </summary>
    private static List<string> FillFields(Anime keep, Anime remove)
    {
        var dropped = new List<string>();
        int? Id(int? kept, int? other, string label)
        {
            if (kept is > 0) { if (other is > 0 && other != kept) dropped.Add($"{label} {other}"); return kept; }
            return other is > 0 ? other : kept;
        }
        keep.AniListId = Id(keep.AniListId, remove.AniListId, "AniList");
        keep.MALId = Id(keep.MALId, remove.MALId, "MAL");
        keep.KitsuId = Id(keep.KitsuId, remove.KitsuId, "Kitsu");

        // A name a person chose outranks one from the sources; an English name outranks a romaji one.
        static bool PersonNamed(Anime anime) => anime.NamesReviewed && !anime.NameFromSources;
        if (!PersonNamed(keep) && (PersonNamed(remove) || (!keep.NamesReviewed && !keep.HasEnglishTitle && remove.HasEnglishTitle)) &&
            !string.IsNullOrWhiteSpace(remove.Title))
        {
            keep.Title = remove.Title;
            keep.HasEnglishTitle = remove.HasEnglishTitle;
            keep.NamesReviewed = remove.NamesReviewed;
            keep.NameFromSources = remove.NameFromSources;
        }

        static string? Text(string? kept, string? other) => string.IsNullOrWhiteSpace(kept) ? other : kept;
        keep.OriginalTitle = Text(keep.OriginalTitle, remove.OriginalTitle);
        keep.Synopsis = Text(keep.Synopsis, remove.Synopsis);
        keep.Type = Text(keep.Type, remove.Type);
        keep.Status = Text(keep.Status, remove.Status);
        if (string.IsNullOrWhiteSpace(keep.ImageUrl))
        {
            keep.ImageUrl = remove.ImageUrl;
            keep.LocalImagePath = remove.LocalImagePath;
        }
        else keep.LocalImagePath = Text(keep.LocalImagePath, remove.LocalImagePath);
        keep.Episodes ??= remove.Episodes;
        keep.Duration ??= remove.Duration;
        keep.Rating ??= remove.Rating;
        keep.StartDate ??= remove.StartDate;
        keep.EndDate ??= remove.EndDate;
        keep.CachedAt = new[] { keep.CachedAt, remove.CachedAt }.Max();
        return dropped;
    }

    /// <summary>Stores the removed row's names, and the kept row's old title if it changed, as aliases.</summary>
    private static int MoveNames(AnimeDbContext db, Anime keep, Anime remove)
    {
        var originalTitle = (string?)db.Entry(keep).Property(anime => anime.Title).OriginalValue;
        var held = keep.TitleAliases.Select(alias => alias.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var name in new[] { originalTitle, remove.Title, remove.OriginalTitle }.Concat(remove.TitleAliases.Select(alias => alias.Title)))
        {
            if (string.IsNullOrWhiteSpace(name) || !held.Add(name.Trim())) continue;
            db.AnimeTitleAliases.Add(new AnimeTitleAlias { AnimeId = keep.Id, Title = name.Trim() });
            added++;
        }
        return added;
    }

    /// <summary>
    /// Moves the removed row's images the kept row does not already have, keeping their IDs so a preferred
    /// image still points at one. The kept row takes the removed row's preferred image if it has none.
    /// </summary>
    private static async Task<int> MoveImagesAsync(AnimeDbContext db, Anime keep, Anime remove, CancellationToken ct)
    {
        var held = keep.Images.ToDictionary(image => (image.Source, image.Type, image.ImageUrl), image => image.Id);
        var imageIds = new Dictionary<int, int>();
        var toMove = new List<int>();
        foreach (var image in remove.Images)
        {
            if (held.TryGetValue((image.Source, image.Type, image.ImageUrl), out var heldId)) imageIds[image.Id] = heldId;
            else { imageIds[image.Id] = image.Id; toMove.Add(image.Id); held[(image.Source, image.Type, image.ImageUrl)] = image.Id; }
        }
        if (toMove.Count > 0)
            await db.AnimeImages.Where(image => toMove.Contains(image.Id)).ExecuteUpdateAsync(set => set.SetProperty(image => image.AnimeId, keep.Id), ct);
        if (keep.PreferredImageId is null && remove.PreferredImageId is int preferred && imageIds.TryGetValue(preferred, out var mapped))
            keep.PreferredImageId = mapped;
        return toMove.Count;
    }

    /// <summary>Moves the English titles of sources the kept row has no title from.</summary>
    private static async Task MoveSourceTitlesAsync(AnimeDbContext db, Anime keep, Anime remove, CancellationToken ct)
    {
        var held = keep.SourceTitles.Select(title => title.Source).ToHashSet(StringComparer.Ordinal);
        var toMove = remove.SourceTitles.Where(title => !held.Contains(title.Source)).Select(title => title.Id).ToList();
        if (toMove.Count > 0)
            await db.AnimeSourceTitles.Where(title => toMove.Contains(title.Id)).ExecuteUpdateAsync(set => set.SetProperty(title => title.AnimeId, keep.Id), ct);
    }

    /// <summary>Gives the kept row every genre, theme and demographic either row had.</summary>
    private static async Task MoveTagsAsync(AnimeDbContext db, int keepId, int removeId, CancellationToken ct)
    {
        var genres = await db.AnimeGenres.Where(tag => tag.AnimeId == removeId && !db.AnimeGenres.Any(held => held.AnimeId == keepId && held.GenreId == tag.GenreId))
            .Select(tag => tag.GenreId).ToListAsync(ct);
        var themes = await db.AnimeThemes.Where(tag => tag.AnimeId == removeId && !db.AnimeThemes.Any(held => held.AnimeId == keepId && held.ThemeId == tag.ThemeId))
            .Select(tag => tag.ThemeId).ToListAsync(ct);
        var demographics = await db.AnimeDemographics.Where(tag => tag.AnimeId == removeId && !db.AnimeDemographics.Any(held => held.AnimeId == keepId && held.DemographicId == tag.DemographicId))
            .Select(tag => tag.DemographicId).ToListAsync(ct);
        db.AnimeGenres.AddRange(genres.Select(id => new AnimeGenre { AnimeId = keepId, GenreId = id }));
        db.AnimeThemes.AddRange(themes.Select(id => new AnimeTheme { AnimeId = keepId, ThemeId = id }));
        db.AnimeDemographics.AddRange(demographics.Select(id => new AnimeDemographic { AnimeId = keepId, DemographicId = id }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts the kept row in every collection the removed row was in, keeping the earlier added date.</summary>
    private static async Task<int> MoveCollectionItemsAsync(AnimeDbContext db, int keepId, int removeId, CancellationToken ct)
    {
        var items = await db.AnimeCollectionItems.AsNoTracking().Where(item => item.AnimeId == removeId).ToListAsync(ct);
        if (items.Count == 0) return 0;
        var held = await db.AnimeCollectionItems.Where(item => item.AnimeId == keepId).ToDictionaryAsync(item => item.CollectionId, ct);
        foreach (var item in items)
        {
            if (held.TryGetValue(item.CollectionId, out var existing)) { if (item.AddedAt < existing.AddedAt) existing.AddedAt = item.AddedAt; }
            else db.AnimeCollectionItems.Add(new AnimeCollectionItem { CollectionId = item.CollectionId, AnimeId = keepId, AddedAt = item.AddedAt });
        }
        await db.SaveChangesAsync(ct);
        return items.Count;
    }

    /// <summary>Re-points recommendations from and to the removed row, dropping any that would link the kept row to itself.</summary>
    private static async Task MoveRecommendationsAsync(AnimeDbContext db, int keepId, int removeId, CancellationToken ct)
    {
        var rows = await db.AnimeRecommendations.AsNoTracking()
            .Where(item => item.SourceAnimeId == removeId || item.RecommendedAnimeId == removeId).ToListAsync(ct);
        if (rows.Count == 0) return;
        var held = (await db.AnimeRecommendations.AsNoTracking().Where(item => item.SourceAnimeId == keepId || item.RecommendedAnimeId == keepId)
            .Select(item => new { item.SourceAnimeId, item.RecommendedAnimeId }).ToListAsync(ct))
            .Select(item => (item.SourceAnimeId, item.RecommendedAnimeId)).ToHashSet();
        foreach (var row in rows)
        {
            var source = row.SourceAnimeId == removeId ? keepId : row.SourceAnimeId;
            var recommended = row.RecommendedAnimeId == removeId ? keepId : row.RecommendedAnimeId;
            if (source == recommended || !held.Add((source, recommended))) continue;
            db.AnimeRecommendations.Add(new AnimeRecommendation { SourceAnimeId = source, RecommendedAnimeId = recommended, Rating = row.Rating, CachedAt = row.CachedAt });
        }
        await db.SaveChangesAsync(ct); // The removed row's own links go when it is deleted.
    }

    /// <summary>
    /// Franchise links are stored by MAL ID. When the kept row has a different MAL ID from the removed row,
    /// links from and to the removed row's ID are moved to the kept row's, unless another row still uses it.
    /// </summary>
    private static async Task MoveRelationsAsync(AnimeDbContext db, Anime keep, Anime remove, CancellationToken ct)
    {
        if (remove.MALId is not int oldId || oldId <= 0 || keep.MALId is not int newId || newId <= 0 || oldId == newId) return;
        if (await db.Animes.AnyAsync(anime => anime.MALId == oldId && anime.Id != remove.Id && anime.Id != keep.Id, ct)) return;
        var rows = await db.CachedAnimeRelations.Where(item => item.RootMalId == oldId || item.RelatedMalId == oldId).ToListAsync(ct);
        if (rows.Count == 0) return;
        var held = (await db.CachedAnimeRelations.AsNoTracking().Where(item => item.RootMalId == newId || item.RelatedMalId == newId)
            .Select(item => new { item.RootMalId, item.RelatedMalId, item.RelationType }).ToListAsync(ct))
            .Select(item => (item.RootMalId, item.RelatedMalId, item.RelationType)).ToHashSet();
        foreach (var row in rows)
        {
            var root = row.RootMalId == oldId ? newId : row.RootMalId;
            var related = row.RelatedMalId == oldId ? newId : row.RelatedMalId;
            if (root == related || !held.Add((root, related, row.RelationType))) { db.CachedAnimeRelations.Remove(row); continue; }
            row.RootMalId = root;
            row.RelatedMalId = related;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves remembered answers to the kept row. Where both rows have an answer for the same provider record
    /// the newer one wins, and a "not the same" answer about the kept row's own provider IDs is dropped,
    /// since the kept row now is that record.
    /// </summary>
    private static async Task<int> MoveDecisionsAsync(AnimeDbContext db, Anime keep, int removeId, CancellationToken ct)
    {
        var decisions = await db.AnimeMatchDecisions.Where(decision => decision.AnimeId == keep.Id || decision.AnimeId == removeId).ToListAsync(ct);
        var movedCount = decisions.Count(decision => decision.AnimeId == removeId);
        var kept = new List<AnimeMatchDecision>();
        foreach (var decision in decisions.OrderByDescending(decision => decision.DecidedAt).ThenByDescending(decision => decision.Id))
        {
            if ((!decision.IsSameAnime && decision.AppliesTo(keep.AniListId, keep.MALId)) ||
                kept.Any(other => other.AppliesTo(decision.SourceAniListId, decision.SourceMalId)))
            {
                db.AnimeMatchDecisions.Remove(decision);
                continue;
            }
            decision.AnimeId = keep.Id;
            kept.Add(decision);
        }
        await db.SaveChangesAsync(ct);
        return movedCount;
    }

    /// <summary>
    /// Replaces the removed row with the kept one in every review's candidates and stored evidence, and in
    /// resolved reviews' results. A review merged into the removed row loses its pre-merge copy: restoring
    /// the kept row to the removed row's old state would be wrong.
    /// </summary>
    private static async Task<int> MoveReviewsAsync(AnimeDbContext db, int keepId, int removeId, CancellationToken ct)
    {
        var idText = removeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var reviews = await db.PendingAnimeReviews
            .Where(review => review.ResolvedAnimeId == removeId || review.CandidateAnimeIdsJson.Contains(idText))
            .ToListAsync(ct);
        var changed = 0;
        foreach (var review in reviews)
        {
            var touched = false;
            var candidates = JsonSerializer.Deserialize<List<int>>(review.CandidateAnimeIdsJson) ?? [];
            if (candidates.Contains(removeId))
            {
                review.CandidateAnimeIdsJson = JsonSerializer.Serialize(candidates.Select(id => id == removeId ? keepId : id).Distinct());
                if (!string.IsNullOrWhiteSpace(review.MatchEvidenceJson))
                {
                    var matches = JsonSerializer.Deserialize<List<CandidateMatch>>(review.MatchEvidenceJson) ?? [];
                    var keptHasMatch = matches.Any(match => match.AnimeId == keepId);
                    matches = matches.Where(match => match.AnimeId != removeId || !keptHasMatch)
                        .Select(match => match.AnimeId == removeId ? new CandidateMatch { AnimeId = keepId, Score = match.Score, Evidence = match.Evidence } : match)
                        .ToList();
                    review.MatchEvidenceJson = JsonSerializer.Serialize(matches);
                    review.MatchScore = matches.Count > 0 ? matches.Max(match => match.Score) : null;
                }
                touched = true;
            }
            if (review.ResolvedAnimeId == removeId)
            {
                review.ResolvedAnimeId = keepId;
                review.PreMergeSnapshotJson = null;
                touched = true;
            }
            if (touched) changed++;
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }
}

/// <summary>
/// The outcome of a duplicate merge: what moved across, as "2 requests", and the removed row's provider IDs
/// that differed from the kept row's and were dropped, as "MAL 123".
/// </summary>
public sealed record CatalogueMergeResult(bool Merged, string? Failure, IReadOnlyList<string> Moved, IReadOnlyList<string> DroppedIds)
{
    public static CatalogueMergeResult Failed(string why) => new(false, why, [], []);
}
