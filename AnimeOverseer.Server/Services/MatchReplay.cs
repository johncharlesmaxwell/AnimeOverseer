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
    /// <summary>Why the settings leave it for a person, when they do.</summary>
    public string? Undecided { get; init; }
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
public sealed class MatchReplayReport(List<MatchReplayCase> cases, Dictionary<string, int> skipped, MatchingSettings? settings = null)
{
    /// <summary>
    /// How every merge threshold from the minimum to 100 would have done on these reviews with the
    /// same weights and rules. Scores do not depend on the threshold, so this reuses them.
    /// </summary>
    public List<MatchThresholdRow> Thresholds { get; } = settings is null ? [] : MatchReplay.SweepThresholds(cases, settings);

    /// <summary>Penalties and conflicts that hit the rows you merged into: the signals most likely too strict.</summary>
    public List<MatchSignalTally> PenaltiesOnYourMerges { get; } = MatchReplay.Tally(cases
        .Where(item => item.PersonAnimeId is int id && item.Matches.ContainsKey(id))
        .SelectMany(item => item.Matches[item.PersonAnimeId!.Value].Evidence)
        .Where(evidence => evidence.Kind is MatchEvidenceKind.Negative or MatchEvidenceKind.HardNegative && (evidence.Weight != 0 || evidence.Kind == MatchEvidenceKind.HardNegative)));

    /// <summary>Points that carried the wrong merges: the signals most likely too generous.</summary>
    public List<MatchSignalTally> PointsBehindWrongMerges { get; } = MatchReplay.Tally(cases
        .Where(item => item.Outcome == MatchReplayOutcome.WrongMerge && item.Suggestion?.AnimeId is int id && item.Matches.ContainsKey(id))
        .SelectMany(item => item.Matches[item.Suggestion!.AnimeId!.Value].Evidence)
        .Where(evidence => evidence.Kind == MatchEvidenceKind.Positive && evidence.Weight > 0));

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

/// <summary>How one merge threshold would have decided the replayed reviews.</summary>
public sealed record MatchThresholdRow(int Threshold, int Agreed, int WrongMerges, int WrongCreates, int Abstained, bool IsCurrent)
{
    public int Decided => Agreed + WrongMerges + WrongCreates;
    public double? Precision => Decided > 0 ? (double)Agreed / Decided : null;
}

/// <summary>One signal's appearances in a group of evidence, with the points it moved and how many were hard conflicts.</summary>
public sealed record MatchSignalTally(string Signal, int Count, int Points, int Conflicts);

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
        var undecided = suggestion is null ? MatchScorer.WhyUndecided(matches.Values.ToList(), settings) : null;
        return new MatchReplayCase
        {
            ReviewId = example.ReviewId, Source = example.Source, Candidates = example.Candidates,
            PersonMerged = example.PersonMerged, PersonAnimeId = example.PersonAnimeId,
            Matches = matches, Suggestion = suggestion, Undecided = undecided,
            Outcome = OutcomeOf(suggestion, example.PersonMerged, example.PersonAnimeId)
        };
    }

    public static MatchReplayOutcome OutcomeOf(MatchSuggestion? suggestion, bool personMerged, int? personAnimeId) => suggestion switch
    {
        null => MatchReplayOutcome.Abstained,
        { Decision: "Merge" } merge => personMerged && merge.AnimeId == personAnimeId ? MatchReplayOutcome.Agreed : MatchReplayOutcome.WrongMerge,
        _ => personMerged ? MatchReplayOutcome.WrongCreate : MatchReplayOutcome.Agreed
    };

    /// <summary>Replays the already-scored cases at each merge threshold from the minimum to 100 in steps of 5.</summary>
    public static List<MatchThresholdRow> SweepThresholds(IReadOnlyCollection<MatchReplayCase> cases, MatchingSettings settings)
    {
        var thresholds = Enumerable.Range(0, (100 - MatchingSettings.MinimumMergeThreshold) / 5 + 1)
            .Select(step => MatchingSettings.MinimumMergeThreshold + step * 5)
            .Append(settings.MergeThreshold).Distinct().Order();
        return thresholds.Select(threshold =>
        {
            var at = settings with { MergeThreshold = threshold };
            var outcomes = cases.Select(item => OutcomeOf(MatchScorer.Suggest(item.Matches.Values.ToList(), at), item.PersonMerged, item.PersonAnimeId)).ToList();
            return new MatchThresholdRow(threshold,
                outcomes.Count(outcome => outcome == MatchReplayOutcome.Agreed),
                outcomes.Count(outcome => outcome == MatchReplayOutcome.WrongMerge),
                outcomes.Count(outcome => outcome == MatchReplayOutcome.WrongCreate),
                outcomes.Count(outcome => outcome == MatchReplayOutcome.Abstained),
                threshold == settings.MergeThreshold);
        }).ToList();
    }

    /// <summary>Evidence grouped by signal, most frequent first.</summary>
    public static List<MatchSignalTally> Tally(IEnumerable<MatchEvidence> evidence)
        => evidence.GroupBy(item => item.Signal)
            .Select(group => new MatchSignalTally(group.Key, group.Count(), group.Sum(item => item.Weight), group.Count(item => item.Kind == MatchEvidenceKind.HardNegative)))
            .OrderByDescending(tally => tally.Count).ThenBy(tally => tally.Signal)
            .ToList();

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
