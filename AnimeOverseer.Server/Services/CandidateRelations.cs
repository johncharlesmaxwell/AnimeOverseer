using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// Gives catalogue rows the franchise links the relation sync cached for them, so the scorer can
/// see a relation from the catalogue's side too: a candidate that lists the incoming record as its
/// sequel is not the same anime, even when the incoming provider does not list the link back.
/// </summary>
public static class CandidateRelations
{
    public static async Task AttachAsync(AnimeDbContext db, IEnumerable<Anime> candidates, CancellationToken ct = default)
    {
        var rows = candidates.Where(candidate => candidate.MALId is > 0).DistinctBy(candidate => candidate).ToList();
        if (rows.Count == 0) return;
        var malIds = rows.Select(candidate => candidate.MALId!.Value).Distinct().ToList();
        var links = await db.CachedAnimeRelations.AsNoTracking()
            .Where(relation => malIds.Contains(relation.RootMalId))
            .Select(relation => new { relation.RootMalId, relation.RelatedMalId, relation.RelationType })
            .ToListAsync(ct);
        if (links.Count == 0) return;
        var byRoot = links.ToLookup(link => link.RootMalId);
        foreach (var candidate in rows)
            foreach (var link in byRoot[candidate.MALId!.Value])
                if (!candidate.ProviderRelations.Any(existing => existing.MalId == link.RelatedMalId))
                    candidate.ProviderRelations.Add(new ProviderRelation { MalId = link.RelatedMalId, RelationType = link.RelationType });
    }
}
