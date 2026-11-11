using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// Finds catalogue rows that are probably the same anime. Imports only compare an incoming record
/// with rows that share a name or nearly all title words, so a duplicate the search missed is created
/// without review. This pairs rows more loosely, by any shared name or uncommon title word, and scores
/// each pair as if the newer row were arriving now.
/// </summary>
public static class CatalogueDuplicateAudit
{
    /// <summary>
    /// A name or word used by more rows than this is too common to pair on: comparing every pair of
    /// forty "Gundam" entries finds sequels, not duplicates, and costs most of the scan.
    /// </summary>
    public const int MaxRowsPerKey = 40;

    /// <summary>
    /// Pairs of row IDs (lower first) that share a provider ID, a compact name, a name with romaji
    /// long vowels folded, or a title word used by no more than <paramref name="maxRowsPerKey"/> rows.
    /// </summary>
    public static HashSet<(int Low, int High)> CandidatePairs(IEnumerable<Anime> rows, int maxRowsPerKey = MaxRowsPerKey)
    {
        var groups = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        void Add(string key, int id)
        {
            if (!groups.TryGetValue(key, out var ids)) groups[key] = ids = [];
            ids.Add(id);
        }

        foreach (var row in rows)
        {
            // Rows sharing a provider ID should never exist; pair them whatever their names.
            if (row.AniListId is > 0) Add("a:" + row.AniListId, row.Id);
            if (row.MALId is > 0) Add("m:" + row.MALId, row.Id);
            foreach (var name in Names(row))
            {
                var compact = TitleNormalizer.Compact(name);
                if (compact.Length < 3) continue;
                Add("n:" + compact, row.Id);
                Add("r:" + TitleNormalizer.RomajiKey(name), row.Id);
                foreach (var token in TitleNormalizer.ComparisonTokens(name, foldRomaji: true))
                    if (token.Length >= 3) Add("t:" + token, row.Id); // "~" plus at least two letters
            }
        }

        var pairs = new HashSet<(int, int)>();
        foreach (var ids in groups.Values)
        {
            if (ids.Count < 2 || ids.Count > maxRowsPerKey) continue;
            var sorted = ids.Order().ToArray();
            for (var i = 0; i < sorted.Length; i++)
                for (var j = i + 1; j < sorted.Length; j++)
                    pairs.Add((sorted[i], sorted[j]));
        }
        return pairs;
    }

    /// <summary>
    /// Scores each pair with the newer row as the incoming record, and keeps those with no hard conflict
    /// that reach <paramref name="minimumScore"/> and that a person has not already called different.
    /// Pairs sharing a provider ID are always kept and come first; then best score first.
    /// </summary>
    public static List<DuplicatePair> Score(
        IReadOnlyDictionary<int, Anime> rows,
        IEnumerable<(int Low, int High)> pairs,
        MatchScoring scoring,
        CatalogueNameStats stats,
        IReadOnlyCollection<AnimeMatchDecision> decisions,
        int minimumScore)
    {
        var rejected = decisions.Where(decision => !decision.IsSameAnime).ToLookup(decision => decision.AnimeId);
        var countedNames = new Dictionary<int, HashSet<string>>();
        var found = new List<DuplicatePair>();
        foreach (var (low, high) in pairs)
        {
            if (!rows.TryGetValue(low, out var older) || !rows.TryGetValue(high, out var newer)) continue;
            var shared = SharedProviderIds(newer, older);
            // Most pairs from a shared word are told apart by provider IDs or years; skip those before
            // the costlier title scoring, which would only mark them as hard conflicts.
            if (shared is null && RuledOutCheaply(newer, older, scoring)) continue;
            if (IsRejected(rejected, newer, older)) continue;
            // The newer row's own names are in the catalogue counts, which an incoming record's would
            // not be, so leave them out, as the replay does for a decision's added names.
            if (!countedNames.TryGetValue(newer.Id, out var own)) countedNames[newer.Id] = own = MatchReplay.CountedNames(newer);
            int Usage(string name) => Math.Max(0, stats.Usage(name) - (own.Contains(name) ? 1 : 0));
            var match = MatchScorer.Score(newer, older, scoring, Usage, stats.TokenWeight);
            if (shared is null && (match.Score < minimumScore || MatchScorer.HasHardNegative(match))) continue;
            found.Add(new DuplicatePair(newer, older, match, shared));
        }
        return found.OrderByDescending(pair => pair.SharedProviderIds is not null)
            .ThenByDescending(pair => pair.Match.Score).ThenByDescending(pair => pair.Newer.Id).ToList();
    }

    /// <summary>The provider IDs both rows carry, as "AniList 123 · MAL 456", or null when they share none.</summary>
    public static string? SharedProviderIds(Anime left, Anime right)
    {
        var shared = new List<string>();
        if (left.AniListId is > 0 && left.AniListId == right.AniListId) shared.Add($"AniList {left.AniListId}");
        if (left.MALId is > 0 && left.MALId == right.MALId) shared.Add($"MAL {left.MALId}");
        return shared.Count > 0 ? string.Join(" · ", shared) : null;
    }

    /// <summary>
    /// True when the scorer would certainly find a hard conflict: differing provider IDs, or start dates
    /// more than two years apart when a year apart is a conflict (a date that may give only its year
    /// still leaves them over a year apart).
    /// </summary>
    public static bool RuledOutCheaply(Anime left, Anime right, MatchScoring scoring)
    {
        static bool Differ(int? a, int? b) => a is > 0 && b is > 0 && a != b;
        if (Differ(left.AniListId, right.AniListId) || Differ(left.MALId, right.MALId) || Differ(left.KitsuId, right.KitsuId)) return true;
        return scoring.StartYearApartIsConflict && left.StartDate is { } a && right.StartDate is { } b && Math.Abs((a.Date - b.Date).TotalDays) > 731;
    }

    /// <summary>True when a remembered answer says either row's provider record is not the other row.</summary>
    public static bool IsRejected(ILookup<int, AnimeMatchDecision> rejectedByAnime, Anime left, Anime right)
        => rejectedByAnime[right.Id].Any(decision => decision.AppliesTo(left.AniListId, left.MALId)) ||
           rejectedByAnime[left.Id].Any(decision => decision.AppliesTo(right.AniListId, right.MALId));

    private static IEnumerable<string> Names(Anime anime)
        => new[] { anime.Title, anime.OriginalTitle }.Concat(anime.TitleAliases.Select(alias => alias.Title))
            .OfType<string>().Where(title => !string.IsNullOrWhiteSpace(title)).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Two catalogue rows that may be the same anime, scored with <see cref="Newer"/> as the incoming record.</summary>
public sealed record DuplicatePair(Anime Newer, Anime Older, CandidateMatch Match, string? SharedProviderIds = null)
{
    /// <summary>
    /// A "not the same" answer is stored against a provider ID, so one of the rows needs one. Rows sharing
    /// a provider ID are the same entry on that provider, so that answer would not mean anything.
    /// </summary>
    public bool CanDismiss => SharedProviderIds is null && (HasProviderId(Newer) || HasProviderId(Older));

    public static bool HasProviderId(Anime anime) => anime.AniListId is > 0 || anime.MALId is > 0;
}

/// <summary>The outcome of one duplicate scan.</summary>
public sealed record DuplicateAuditResult(List<DuplicatePair> Pairs, int RowsScanned, int PairsCompared, int MinimumScore, TimeSpan Elapsed);
