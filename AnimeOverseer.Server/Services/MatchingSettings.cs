using System.Globalization;
using System.Text.Json;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>How catalogue match suggestions are used.</summary>
public enum AutoDecisionMode
{
    /// <summary>No suggestions are shown or applied.</summary>
    Off,
    /// <summary>Dry run: suggestions are shown on the review page but never applied.</summary>
    Suggest,
    /// <summary>Suggestions are applied after imports and provider-ID checks.</summary>
    Apply
}

/// <summary>
/// Automatic catalogue-match decision settings. Read straight from AppSettings
/// rather than through SettingsService so a saved change applies to the very
/// next import instead of after SettingsService's cache expires.
/// </summary>
public sealed record MatchingSettings(AutoDecisionMode Mode, int MergeThreshold, int DeferDays = 30)
{
    public const string ModeKey = "matching.autoDecisionMode";
    public const string MergeThresholdKey = "matching.autoMergeThreshold";
    public const string DeferDaysKey = "matching.deferDays";
    public const string MarginMergeEnabledKey = "matching.marginMergeEnabled";
    public const string MergeMarginKey = "matching.mergeMargin";
    public const string LowScoreCreateEnabledKey = "matching.lowScoreCreateEnabled";
    public const string CreateBelowScoreKey = "matching.createBelowScore";
    public const string ScoringKey = "matching.scoring";
    public const int MinimumMergeThreshold = 50;
    public const int MaximumDeferDays = 365;

    // Suggest-only until someone has watched the suggestions against a real queue.
    public static readonly MatchingSettings Default = new(AutoDecisionMode.Suggest, 90, 30);

    /// <summary>
    /// Also merge when several candidates are conflict-free but the best one reaches
    /// the threshold and leads the runner-up by at least <see cref="MergeMargin"/>.
    /// </summary>
    public bool MarginMergeEnabled { get; init; }
    public int MergeMargin { get; init; } = 25;

    /// <summary>Create as new when every conflict-free candidate scores below <see cref="CreateBelowScore"/>.</summary>
    public bool LowScoreCreateEnabled { get; init; }
    public int CreateBelowScore { get; init; } = 20;

    /// <summary>The points each signal adds or removes when scoring a candidate.</summary>
    public MatchScoring Scoring { get; init; } = MatchScoring.Default;

    public static MatchingSettings Read(string? mode, string? mergeThreshold, string? deferDays = null) => new(
        Enum.TryParse<AutoDecisionMode>(mode, ignoreCase: true, out var parsedMode) && Enum.IsDefined(parsedMode) ? parsedMode : Default.Mode,
        int.TryParse(mergeThreshold, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, MinimumMergeThreshold, 100) : Default.MergeThreshold,
        int.TryParse(deferDays, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            ? Math.Clamp(days, 1, MaximumDeferDays) : Default.DeferDays);

    /// <summary>Reads every stored value, falling back to defaults and clamping to valid ranges.</summary>
    public static MatchingSettings Read(IReadOnlyDictionary<string, string?> values)
    {
        var basic = Read(values.GetValueOrDefault(ModeKey), values.GetValueOrDefault(MergeThresholdKey), values.GetValueOrDefault(DeferDaysKey));
        MatchScoring? scoring = null;
        if (values.GetValueOrDefault(ScoringKey) is { Length: > 0 } json)
        {
            try { scoring = JsonSerializer.Deserialize<MatchScoring>(json); }
            catch (JsonException) { scoring = null; }
        }
        return (basic with
        {
            MarginMergeEnabled = bool.TryParse(values.GetValueOrDefault(MarginMergeEnabledKey), out var margin) && margin,
            MergeMargin = Int(values.GetValueOrDefault(MergeMarginKey), Default.MergeMargin),
            LowScoreCreateEnabled = bool.TryParse(values.GetValueOrDefault(LowScoreCreateEnabledKey), out var lowCreate) && lowCreate,
            CreateBelowScore = Int(values.GetValueOrDefault(CreateBelowScoreKey), Default.CreateBelowScore),
            Scoring = scoring ?? MatchScoring.Default
        }).Normalized();
    }

    /// <summary>A copy with every value inside the range the scorer and decision bands accept.</summary>
    public MatchingSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : Default.Mode,
        MergeThreshold = Math.Clamp(MergeThreshold, MinimumMergeThreshold, 100),
        DeferDays = Math.Clamp(DeferDays, 1, MaximumDeferDays),
        MergeMargin = Math.Clamp(MergeMargin, 1, 100),
        // Creating must stay below merging, or one score could mean both.
        CreateBelowScore = Math.Clamp(CreateBelowScore, 1, MinimumMergeThreshold),
        Scoring = (Scoring ?? MatchScoring.Default).Normalized()
    };

    public static async Task<MatchingSettings> LoadAsync(AnimeDbContext db, CancellationToken ct = default)
    {
        var values = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith("matching."))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
        return Read(values);
    }

    public static async Task SaveAsync(AnimeDbContext db, MatchingSettings settings, CancellationToken ct = default)
    {
        var stored = settings.Normalized();
        await SetAsync(db, ModeKey, stored.Mode.ToString(), ct);
        await SetAsync(db, MergeThresholdKey, stored.MergeThreshold.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, DeferDaysKey, stored.DeferDays.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, MarginMergeEnabledKey, stored.MarginMergeEnabled.ToString(), ct);
        await SetAsync(db, MergeMarginKey, stored.MergeMargin.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, LowScoreCreateEnabledKey, stored.LowScoreCreateEnabled.ToString(), ct);
        await SetAsync(db, CreateBelowScoreKey, stored.CreateBelowScore.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, ScoringKey, JsonSerializer.Serialize(stored.Scoring), ct);
        await db.SaveChangesAsync(ct);
    }

    private static int Int(string? value, int fallback)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static async Task SetAsync(AnimeDbContext db, string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(setting => setting.Key == key, ct);
        if (existing is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else existing.Value = value;
    }
}

/// <summary>
/// Points for each match signal. Bonuses add to the score and penalties subtract
/// from it (both are stored as positive numbers). The total is clamped to 0–100.
/// </summary>
public sealed record MatchScoring
{
    public static readonly MatchScoring Default = new();

    // Titles: only the strongest title signal counts.
    public int MainTitleExact { get; init; } = 45;
    public int OriginalTitleExact { get; init; } = 40;
    public int AliasMatch { get; init; } = 30;
    /// <summary>Points for fully similar titles; partial similarity earns a share of them.</summary>
    public int SimilarTitleMax { get; init; } = 30;
    public int SeasonMismatchPenalty { get; init; } = 25;
    /// <summary>Divide alias points by the number of catalogue rows sharing that alias.</summary>
    public bool ScaleSharedAliases { get; init; } = true;
    /// <summary>Treat "Yuuki", "Yūki" and "Yuki" as the same title when no exact title matches.</summary>
    public bool MatchRomajiSpellings { get; init; } = true;

    // Start date.
    public int StartWithinMonth { get; init; } = 25;
    public int StartWithinSixMonths { get; init; } = 10;
    public int StartUnderYearPenalty { get; init; } = 10;
    public bool StartYearApartIsConflict { get; init; } = true;
    /// <summary>Used instead of a hard conflict when <see cref="StartYearApartIsConflict"/> is off.</summary>
    public int StartYearApartPenalty { get; init; } = 30;

    // Format.
    public int FormatSame { get; init; } = 10;
    public int FormatDifferPenalty { get; init; } = 5;
    /// <summary>When off, movie vs series costs <see cref="FormatDifferPenalty"/> like any other format difference.</summary>
    public bool MovieVsSeriesIsConflict { get; init; } = true;

    // Episodes.
    public int EpisodesSame { get; init; } = 15;
    public int EpisodesNearly { get; init; } = 8;
    public int EpisodesDifferPenalty { get; init; } = 10;
    /// <summary>Counts this far apart (beyond one) are neutral rather than penalised.</summary>
    public int EpisodeTolerance { get; init; } = 2;
    /// <summary>Differing counts are neutral when either show is still airing or not yet aired.</summary>
    public bool IgnoreEpisodesWhileAiring { get; init; } = true;

    // Episode length.
    public int DurationSimilar { get; init; } = 5;
    /// <summary>Applied when one side has short episodes (15 min or less) and the other full-length (20 min or more).</summary>
    public int DurationDifferPenalty { get; init; } = 10;

    // Bands.
    /// <summary>The highest score a candidate with a hard conflict can have, so conflicts sort last.</summary>
    public int HardConflictCap { get; init; } = 10;
    public int HighConfidence { get; init; } = 80;
    public int MediumConfidence { get; init; } = 50;

    public MatchScoring Normalized()
    {
        static int Points(int value) => Math.Clamp(value, 0, 100);
        var high = Math.Clamp(HighConfidence, 1, 100);
        return this with
        {
            MainTitleExact = Points(MainTitleExact), OriginalTitleExact = Points(OriginalTitleExact), AliasMatch = Points(AliasMatch),
            SimilarTitleMax = Points(SimilarTitleMax), SeasonMismatchPenalty = Points(SeasonMismatchPenalty),
            StartWithinMonth = Points(StartWithinMonth), StartWithinSixMonths = Points(StartWithinSixMonths),
            StartUnderYearPenalty = Points(StartUnderYearPenalty), StartYearApartPenalty = Points(StartYearApartPenalty),
            FormatSame = Points(FormatSame), FormatDifferPenalty = Points(FormatDifferPenalty),
            EpisodesSame = Points(EpisodesSame), EpisodesNearly = Points(EpisodesNearly), EpisodesDifferPenalty = Points(EpisodesDifferPenalty),
            EpisodeTolerance = Math.Clamp(EpisodeTolerance, 1, 50),
            DurationSimilar = Points(DurationSimilar), DurationDifferPenalty = Points(DurationDifferPenalty),
            HardConflictCap = Points(HardConflictCap),
            HighConfidence = high,
            MediumConfidence = Math.Clamp(MediumConfidence, 0, high)
        };
    }

    /// <summary>
    /// The points a candidate earns when every signal agrees: strongest title, close start, same format,
    /// episodes and length. The defaults add up to exactly 100; a final score is held to 0-100.
    /// </summary>
    public int MaximumScore =>
        new[] { MainTitleExact, OriginalTitleExact, AliasMatch, SimilarTitleMax }.Max() + StartWithinMonth + FormatSame + EpisodesSame + DurationSimilar;
}
