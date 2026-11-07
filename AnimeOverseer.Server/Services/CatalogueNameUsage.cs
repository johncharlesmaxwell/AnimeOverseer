using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// How many catalogue rows use each compact name (main title, original title, or
/// alias). A name used by several rows, such as a franchise name, is weak evidence
/// that two records are the same anime.
/// </summary>
public static class CatalogueNameUsage
{
    public static async Task<Func<string, int>> LoadAsync(AnimeDbContext db, CancellationToken ct = default)
    {
        var titles = await db.Animes.AsNoTracking().Select(anime => new { anime.Id, anime.Title, anime.OriginalTitle }).ToListAsync(ct);
        var aliases = await db.AnimeTitleAliases.AsNoTracking().Select(alias => new { alias.AnimeId, alias.Title }).ToListAsync(ct);
        var names = titles.SelectMany(row => new[] { (row.Id, row.Title), (row.Id, row.OriginalTitle) })
            .Concat(aliases.Select(alias => (Id: alias.AnimeId, Title: (string?)alias.Title)));
        var usage = Count(names);
        return name => usage.GetValueOrDefault(name, 0);
    }

    /// <summary>Distinct row IDs per compact name.</summary>
    public static Dictionary<string, int> Count(IEnumerable<(int Id, string? Title)> names)
        => names.Select(entry => (entry.Id, Name: TitleNormalizer.Compact(entry.Title)))
            .Where(entry => entry.Name.Length > 0)
            .Distinct()
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}
