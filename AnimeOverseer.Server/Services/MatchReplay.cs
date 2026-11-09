using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>How a replayed decision compares with what the person decided.</summary>
public enum MatchReplayOutcome
{
    /// <summary>The settings make the same decision the person made.</summary>
    Agreed,
    /// <summary>The settings merge, but the person created as new or merged into a different row.</summary>
    WrongMerge,
    /// <summary>The settings create as new, but the person merged.</summary>
    WrongCreate,
    /// <summary>The settings leave the review for a person.</summary>
    Abstained
}

/// <summary>
/// One review a person decided, rebuilt as it looked before the decision: the incoming
/// record, its candidates (the merge target as it was before the merge), and the names
/// the decision added to the catalogue, which name-usage counts must not include.
/// </summary>
public sealed record MatchReplayExample(
    int ReviewId,
    Anime Source,
    IReadOnlyList<Anime> Candidates,
    bool PersonMerged,
    int? PersonAnimeId,
    IReadOnlySet<string> NamesAddedByDecision);

public sealed class MatchReplayCase
{
    public int ReviewId { get; init; }
    public Anime Source { get; init; } = null!;
    public IReadOnlyList<Anime> Candidates { get; init; } = [];
    public bool PersonMerged { get; init; }
    public int? PersonAnimeId { get; init; }
    public Dictionary<int, CandidateMatch> Matches { get; init; } = [];
    public MatchSuggestion? Suggestion { get; init; }
    public MatchReplayOutcome Outcome { get; init; }

    /// <summary>The score of the row the person merged into, or null when they created as new.</summary>
    public int? PersonChoiceScore => PersonAnimeId is int id ? Matches.GetValueOrDefault(id)?.Score : null;

    /// <summary>True when the person merged and their row outscored every other candidate.</summary>
    public bool PersonChoiceIsTop => PersonChoiceScore is int chosen &&
        Matches.Values.Where(match => match.AnimeId != PersonAnimeId).All(match => match.Score < chosen);

    /// <summary>True when the person merged into a row the scorer marks as a hard conflict.</summary>
    public bool PersonChoiceHadConflict => PersonAnimeId is int id && Matches.GetValueOrDefault(id) is { } match && MatchScorer.HasHardNegative(match);

    public Anime? CandidateById(int? id) => id is int value ? Candidates.FirstOrDefault(candidate => candidate.Id == value) : null;
}

/// <summary>How a set of matching settings would have decided the reviews people have already decided.</summary>
public sealed class MatchReplayReport(List<MatchReplayCase> cases, Dictionary<string, int> skipped)
{
    public List<MatchReplayCase> Cases { get; } = cases;
    /// <summary>Decided reviews that could not be rebuilt, by reason.</summary>
    public Dictionary<string, int> Skipped { get; } = skipped;

    public int Total => Cases.Count;
    public List<MatchReplayCase> Agreed { get; } = cases.Where(item => item.Outcome == MatchReplayOutcome.Agreed).ToList();
    public List<MatchReplayCase> WrongMerges { get; } = cases.Where(item => item.Outcome == MatchReplayOutcome.WrongMerge).ToList();
    public List<MatchReplayCase> WrongCreates { get; } = cases.Where(item => item.Outcome == MatchReplayOutcome.WrongCreate).ToList();
    public List<MatchReplayCase> Abstained { get; } = cases.Where(item => item.Outcome == MatchReplayOutcome.Abstained).ToList();

    /// <summary>Reviews the person merged; the base for <see cref="PersonChoiceNotTop"/>.</summary>
    public int PersonMerges => Cases.Count(item => item.PersonMerged);
    /// <summary>Merges where another candidate scored as high as or higher than the person's choice.</summary>
    public List<MatchReplayCase> PersonChoiceNotTop { get; } = cases.Where(item => item.PersonMerged && !item.PersonChoiceIsTop).ToList();
    /// <summary>Merges into a row the scorer calls a hard conflict: a conflict rule is stricter than the person.</summary>
    public List<MatchReplayCase> PersonChoiceConflicts { get; } = cases.Where(item => item.PersonChoiceHadConflict).ToList();

    /// <summary>Of the reviews the settings would decide, the share decided the way the person did.</summary>
    public double? Precision => Total - Abstained.Count is var decided and > 0 ? (double)Agreed.Count / decided : null;
}

public static class MatchReplay
{
    /// <summary>Scores one rebuilt review under <paramref name="settings"/> and compares the decision with the person's.</summary>
    public static MatchReplayCase Evaluate(MatchReplayExample example, MatchingSettings settings, CatalogueNameStats stats)
    {
        // Leave out the decision's own names, which made the catalogue use them once more.
        int Usage(string name) => Math.Max(0, stats.Usage(name) - (example.NamesAddedByDecision.Contains(name) ? 1 : 0));
        var matches = example.Candidates.DistinctBy(candidate => candidate.Id)
            .ToDictionary(candidate => candidate.Id, candidate => MatchScorer.Score(example.Source, candidate, settings.Scoring, Usage, stats.TokenWeight));
        // Mode is ignored: the point is to see what the rules would do before switching them on.
        var suggestion = MatchScorer.Suggest(matches.Values.ToList(), settings);
        var outcome = suggestion switch
        {
            null => MatchReplayOutcome.Abstained,
            { Decision: "Merge" } merge => example.PersonMerged && merge.AnimeId == example.PersonAnimeId ? MatchReplayOutcome.Agreed : MatchReplayOutcome.WrongMerge,
            _ => example.PersonMerged ? MatchReplayOutcome.WrongCreate : MatchReplayOutcome.Agreed
        };
        return new MatchReplayCase
        {
            ReviewId = example.ReviewId, Source = example.Source, Candidates = example.Candidates,
            PersonMerged = example.PersonMerged, PersonAnimeId = example.PersonAnimeId,
            Matches = matches, Suggestion = suggestion, Outcome = outcome
        };
    }

    /// <summary>Compact forms of the names <see cref="CatalogueNameUsage"/> counts for a row.</summary>
    public static HashSet<string> CountedNames(Anime anime)
        => new[] { anime.Title, anime.OriginalTitle }.Concat(anime.TitleAliases.Select(alias => alias.Title))
            .Select(TitleNormalizer.Compact).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>A merge target as it was before the merge, from the review's restore point.</summary>
    public static Anime BeforeMerge(int animeId, AnimeRestorePoint restore)
    {
        var anime = AnimeCacheService.FromSnapshot(restore.Fields);
        anime.Id = animeId;
        anime.TitleAliases = restore.Aliases.Select(title => new AnimeTitleAlias { AnimeId = animeId, Title = title }).ToList();
        return anime;
    }
}
