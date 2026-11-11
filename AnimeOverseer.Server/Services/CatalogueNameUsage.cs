using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// How many catalogue rows use each compact name (main title, original title, or
/// alias), and each title word. A name or word used by many rows, such as a franchise
/// name or "no", is weak evidence that two records are the same anime.
/// </summary>
public static class CatalogueNameUsage
{
    public static async Task<CatalogueNameStats> LoadAsync(AnimeDbContext db, CancellationToken ct = default)
    {
        var titles = await db.Animes.AsNoTracking().Select(anime => new { anime.Id, anime.Title, anime.OriginalTitle }).ToListAsync(ct);
        var aliases = await db.AnimeTitleAliases.AsNoTracking().Select(alias => new { alias.AnimeId, alias.Title }).ToListAsync(ct);
        var names = titles.SelectMany(row => new[] { (row.Id, row.Title), (row.Id, row.OriginalTitle) })
            .Concat(aliases.Select(alias => (Id: alias.AnimeId, Title: (string?)alias.Title)))
            .ToList();
        return new CatalogueNameStats(Count(names), CountTokens(names), titles.Count);
    }

    /// <summary>The same counts from rows already loaded with their aliases.</summary>
    public static CatalogueNameStats From(IReadOnlyCollection<Anime> rows)
    {
        var names = rows.SelectMany(row => new[] { (row.Id, row.Title), (row.Id, row.OriginalTitle) }
            .Concat(row.TitleAliases.Select(alias => (row.Id, (string?)alias.Title)))).ToList();
        return new CatalogueNameStats(Count(names), CountTokens(names), rows.Count);
    }

    /// <summary>Distinct row IDs per compact name.</summary>
    public static Dictionary<string, int> Count(IEnumerable<(int Id, string? Title)> names)
        => names.Select(entry => (entry.Id, Name: TitleNormalizer.Compact(entry.Title)))
            .Where(entry => entry.Name.Length > 0)
            .Distinct()
            .GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>
    /// Distinct row IDs per comparison token, in both the plain and romaji-folded forms
    /// of <see cref="TitleNormalizer.ComparisonTokens"/>.
    /// </summary>
    public static Dictionary<string, int> CountTokens(IEnumerable<(int Id, string? Title)> names)
    {
        var rowsByToken = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (id, title) in names)
        {
            if (string.IsNullOrWhiteSpace(title)) continue;
            foreach (var token in TitleNormalizer.ComparisonTokens(title, foldRomaji: false).Concat(TitleNormalizer.ComparisonTokens(title, foldRomaji: true)))
            {
                if (!rowsByToken.TryGetValue(token, out var rows)) rowsByToken[token] = rows = [];
                rows.Add(id);
            }
        }
        return rowsByToken.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.Ordinal);
    }
}

/// <summary>Catalogue-wide name and word frequencies used to discount common names and words.</summary>
public sealed class CatalogueNameStats(Dictionary<string, int> nameUsage, Dictionary<string, int> tokenUsage, int rowCount)
{
    public static readonly CatalogueNameStats Empty = new([], [], 0);

    /// <summary>Rows using a compact name; 0 when none does.</summary>
    public int Usage(string compactName) => nameUsage.GetValueOrDefault(compactName, 0);

    /// <summary>
    /// Inverse document frequency of a comparison token: 1 for a word on every row, rising
    /// to about ln(rows) + 1 for a word on one row or none. With an empty catalogue every
    /// word weighs 1, which is plain Jaccard.
    /// </summary>
    public double TokenWeight(string token)
        => Math.Max(0, Math.Log((rowCount + 1.0) / (tokenUsage.GetValueOrDefault(token, 0) + 1.0))) + 1.0;
}
