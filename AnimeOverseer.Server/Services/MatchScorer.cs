using System.Text.Json.Serialization;
using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// Scores how likely an incoming provider record is the same anime as an existing
/// catalogue row, and records each signal that moved the score. The score only
/// informs review; it never resolves a review by itself.
/// </summary>
public static class MatchScorer
{
    // With default weights a clean exact main-title match with agreeing date,
    // format, and episode count reaches 100. Hard negatives cap the score so
    // they always sort last. Weights are configurable in MatchingSettings.

    public static CandidateMatch Score(Anime source, Anime candidate, MatchScoring? scoring = null)
    {
        var weights = scoring ?? MatchScoring.Default;
        var evidence = new List<MatchEvidence>();
        AddIdEvidence(source, candidate, evidence);
        AddTitleEvidence(source, candidate, weights, evidence);
        AddStartDateEvidence(source.StartDate, candidate.StartDate, weights, evidence);
        AddTypeEvidence(source.Type, candidate.Type, weights, evidence);
        AddEpisodeEvidence(source.Episodes, candidate.Episodes, weights, evidence);

        var score = Math.Clamp(evidence.Sum(item => item.Weight), 0, 100);
        if (evidence.Any(item => item.Kind == MatchEvidenceKind.HardNegative)) score = Math.Min(score, weights.HardConflictCap);
        return new CandidateMatch { AnimeId = candidate.Id, Score = score, Evidence = evidence };
    }

    public static string Confidence(CandidateMatch match, MatchScoring? scoring = null)
    {
        var weights = scoring ?? MatchScoring.Default;
        return HasHardNegative(match) ? "Conflict"
            : match.Score >= weights.HighConfidence ? "High"
            : match.Score >= weights.MediumConfidence ? "Medium"
            : "Low";
    }

    /// <summary>The decision band using only the always-on rules and the given threshold.</summary>
    public static MatchSuggestion? Suggest(IReadOnlyCollection<CandidateMatch> matches, int mergeThreshold)
        => Suggest(matches, new MatchingSettings(AutoDecisionMode.Apply, mergeThreshold));

    /// <summary>
    /// The decision band for a review's scored candidates, or null when a person
    /// must decide. Create when every candidate has a hard conflict; merge when
    /// exactly one candidate is conflict-free and it reaches the threshold. The
    /// optional rules also merge a clear leader and create when nothing scores well.
    /// </summary>
    public static MatchSuggestion? Suggest(IReadOnlyCollection<CandidateMatch> matches, MatchingSettings settings)
    {
        if (matches.Count == 0) return new("Create", null, "no catalogue candidate remains");
        var conflictFree = matches.Where(match => !HasHardNegative(match)).OrderByDescending(match => match.Score).ToList();
        if (conflictFree.Count == 0) return new("Create", null, "every candidate has a hard conflict");
        var best = conflictFree[0];
        if (conflictFree.Count == 1 && best.Score >= settings.MergeThreshold)
            return new("Merge", best.AnimeId, $"the only conflict-free candidate scored {best.Score} (threshold {settings.MergeThreshold})");
        if (settings.MarginMergeEnabled && conflictFree.Count > 1 && best.Score >= settings.MergeThreshold &&
            best.Score - conflictFree[1].Score >= settings.MergeMargin)
            return new("Merge", best.AnimeId, $"the best candidate scored {best.Score} (threshold {settings.MergeThreshold}), {best.Score - conflictFree[1].Score} ahead of the next (margin {settings.MergeMargin})");
        if (settings.LowScoreCreateEnabled && best.Score < settings.CreateBelowScore)
            return new("Create", null, $"the best conflict-free candidate scored {best.Score}, below {settings.CreateBelowScore}");
        return null;
    }

    private static bool HasHardNegative(CandidateMatch match) => match.Evidence.Any(item => item.Kind == MatchEvidenceKind.HardNegative);

    private static void AddIdEvidence(Anime source, Anime candidate, List<MatchEvidence> evidence)
    {
        if (source.AniListId is > 0 && candidate.AniListId is > 0 && source.AniListId != candidate.AniListId)
            evidence.Add(HardNegative("AniList ID", $"Different AniList IDs ({source.AniListId} vs {candidate.AniListId})"));
        if (source.MALId is > 0 && candidate.MALId is > 0 && source.MALId != candidate.MALId)
            evidence.Add(HardNegative("MAL ID", $"Different MAL IDs ({source.MALId} vs {candidate.MALId})"));
        if (RelationTo(source, candidate) is { } relation)
            evidence.Add(HardNegative("Relation", $"Incoming lists this as its {relation.RelationType.Replace('_', ' ').ToLowerInvariant()}"));
    }

    /// <summary>True when the source's provider lists the candidate as a separate franchise entry.</summary>
    public static bool IsRelated(Anime source, Anime candidate) => RelationTo(source, candidate) is not null;

    private static ProviderRelation? RelationTo(Anime source, Anime candidate)
        => source.ProviderRelations.FirstOrDefault(relation =>
            (relation.AniListId is > 0 && relation.AniListId == candidate.AniListId) ||
            (relation.MalId is > 0 && relation.MalId == candidate.MALId));

    private static void AddTitleEvidence(Anime source, Anime candidate, MatchScoring weights, List<MatchEvidence> evidence)
    {
        var sourceMain = TitleNormalizer.Compact(source.Title);
        var sourceOriginal = TitleNormalizer.Compact(source.OriginalTitle);
        var candidateMain = TitleNormalizer.Compact(candidate.Title);
        var candidateOriginal = TitleNormalizer.Compact(candidate.OriginalTitle);
        var sourceNames = AllNames(source).Select(TitleNormalizer.Compact).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);
        var candidateNames = AllNames(candidate).Select(TitleNormalizer.Compact).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal);

        if (sourceMain.Length > 0 && sourceMain == candidateMain)
            evidence.Add(Positive("Title", "Main titles match exactly", weights.MainTitleExact));
        else if (sourceOriginal.Length > 0 && sourceOriginal == candidateOriginal)
            evidence.Add(Positive("Title", "Original (romaji/native) titles match exactly", weights.OriginalTitleExact));
        else if (sourceNames.Overlaps(candidateNames))
            evidence.Add(Positive("Title", "Matched through an alternative title or alias", weights.AliasMatch));
        else
        {
            var similarity = AllNames(source).Select(TitleNormalizer.Tokens).Where(tokens => tokens.Count >= 2)
                .SelectMany(left => AllNames(candidate).Select(TitleNormalizer.Tokens).Where(tokens => tokens.Count >= 2)
                    .Select(right => TitleNormalizer.TokenScore(left, right)))
                .DefaultIfEmpty(0).Max();
            evidence.Add(similarity > 0
                ? Positive("Title", $"Titles are {similarity:P0} similar", (int)Math.Round(weights.SimilarTitleMax * similarity))
                : Negative("Title", "No shared title words", 0));
        }

        // Franchise synonyms often attach a season-one name to a later season.
        // Compare the seasons the main titles name, since aliases rarely carry them.
        var sourceSeason = TitleNormalizer.SeasonNumber(source.Title);
        var candidateSeason = TitleNormalizer.SeasonNumber(candidate.Title);
        if (sourceSeason != candidateSeason)
            evidence.Add(Negative("Season", $"Titles name different seasons ({sourceSeason} vs {candidateSeason})", -weights.SeasonMismatchPenalty));
    }

    private static void AddStartDateEvidence(DateTime? source, DateTime? candidate, MatchScoring weights, List<MatchEvidence> evidence)
    {
        if (source is null || candidate is null)
        {
            evidence.Add(Neutral("Start date", "Start date unknown on one side"));
            return;
        }
        var days = Math.Abs((source.Value - candidate.Value).TotalDays);
        if (days <= 31) evidence.Add(Positive("Start date", "Start dates within a month", weights.StartWithinMonth));
        else if (days <= 183) evidence.Add(Positive("Start date", $"Start dates {days / 30:0} months apart", weights.StartWithinSixMonths));
        else if (days < 365) evidence.Add(Negative("Start date", $"Start dates {days / 30:0} months apart", -weights.StartUnderYearPenalty));
        else
        {
            var detail = $"Start dates {days / 365:0.#} years apart ({source:MMM yyyy} vs {candidate:MMM yyyy})";
            evidence.Add(weights.StartYearApartIsConflict ? HardNegative("Start date", detail) : Negative("Start date", detail, -weights.StartYearApartPenalty));
        }
    }

    private static void AddTypeEvidence(string? source, string? candidate, MatchScoring weights, List<MatchEvidence> evidence)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(candidate))
        {
            evidence.Add(Neutral("Format", "Format unknown on one side"));
            return;
        }
        if (string.Equals(source.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase))
            evidence.Add(Positive("Format", $"Both are {source.Trim()}", weights.FormatSame));
        else if (weights.MovieVsSeriesIsConflict && IsMovie(source) != IsMovie(candidate))
            evidence.Add(HardNegative("Format", $"Movie vs series ({source.Trim()} vs {candidate.Trim()})"));
        else
            evidence.Add(Negative("Format", $"Formats differ ({source.Trim()} vs {candidate.Trim()})", -weights.FormatDifferPenalty));
    }

    private static void AddEpisodeEvidence(int? source, int? candidate, MatchScoring weights, List<MatchEvidence> evidence)
    {
        if (source is not > 0 || candidate is not > 0)
        {
            evidence.Add(Neutral("Episodes", "Episode count unknown on one side"));
            return;
        }
        if (source == candidate) evidence.Add(Positive("Episodes", $"Both have {source} episodes", weights.EpisodesSame));
        else if (Math.Abs(source.Value - candidate.Value) <= 1) evidence.Add(Positive("Episodes", $"Episode counts nearly agree ({source} vs {candidate})", weights.EpisodesNearly));
        else evidence.Add(Negative("Episodes", $"Episode counts differ ({source} vs {candidate})", -weights.EpisodesDifferPenalty));
    }

    private static bool IsMovie(string type) => type.Trim().Equals("movie", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> AllNames(Anime anime)
        => new[] { anime.Title, anime.OriginalTitle }.Concat(anime.AlternativeTitles).Concat(anime.TitleAliases.Select(alias => alias.Title))
            .OfType<string>().Where(title => !string.IsNullOrWhiteSpace(title));

    private static MatchEvidence Positive(string signal, string detail, int weight) => new() { Signal = signal, Detail = detail, Weight = weight, Kind = MatchEvidenceKind.Positive };
    private static MatchEvidence Negative(string signal, string detail, int weight) => new() { Signal = signal, Detail = detail, Weight = weight, Kind = MatchEvidenceKind.Negative };
    private static MatchEvidence Neutral(string signal, string detail) => new() { Signal = signal, Detail = detail, Kind = MatchEvidenceKind.Neutral };
    private static MatchEvidence HardNegative(string signal, string detail) => new() { Signal = signal, Detail = detail, Kind = MatchEvidenceKind.HardNegative };
}

[JsonConverter(typeof(JsonStringEnumConverter<MatchEvidenceKind>))]
public enum MatchEvidenceKind { Positive, Negative, Neutral, HardNegative }

/// <summary>One signal behind a candidate's score, shown to the reviewer.</summary>
public sealed class MatchEvidence
{
    public string Signal { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public int Weight { get; init; }
    public MatchEvidenceKind Kind { get; init; }
}

/// <summary>A scored catalogue candidate for a pending review.</summary>
public sealed class CandidateMatch
{
    public int AnimeId { get; init; }
    public int Score { get; init; }
    public List<MatchEvidence> Evidence { get; init; } = [];
}

/// <summary>An automatic decision band: "Merge" into <see cref="AnimeId"/>, or "Create" as new.</summary>
public sealed record MatchSuggestion(string Decision, int? AnimeId, string Explanation);
