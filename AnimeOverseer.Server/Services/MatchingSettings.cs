using System.Globalization;
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
public sealed record MatchingSettings(AutoDecisionMode Mode, int MergeThreshold)
{
    public const string ModeKey = "matching.autoDecisionMode";
    public const string MergeThresholdKey = "matching.autoMergeThreshold";
    public const int MinimumMergeThreshold = 50;

    // Suggest-only until someone has watched the suggestions against a real queue.
    public static readonly MatchingSettings Default = new(AutoDecisionMode.Suggest, 90);

    public static MatchingSettings Read(string? mode, string? mergeThreshold) => new(
        Enum.TryParse<AutoDecisionMode>(mode, ignoreCase: true, out var parsedMode) && Enum.IsDefined(parsedMode) ? parsedMode : Default.Mode,
        int.TryParse(mergeThreshold, NumberStyles.None, CultureInfo.InvariantCulture, out var threshold)
            ? Math.Clamp(threshold, MinimumMergeThreshold, 100) : Default.MergeThreshold);

    public static async Task<MatchingSettings> LoadAsync(AnimeDbContext db, CancellationToken ct = default)
    {
        var values = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key == ModeKey || setting.Key == MergeThresholdKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
        return Read(values.GetValueOrDefault(ModeKey), values.GetValueOrDefault(MergeThresholdKey));
    }

    public static async Task SaveAsync(AnimeDbContext db, MatchingSettings settings, CancellationToken ct = default)
    {
        var stored = Read(settings.Mode.ToString(), settings.MergeThreshold.ToString(CultureInfo.InvariantCulture));
        await SetAsync(db, ModeKey, stored.Mode.ToString(), ct);
        await SetAsync(db, MergeThresholdKey, stored.MergeThreshold.ToString(CultureInfo.InvariantCulture), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetAsync(AnimeDbContext db, string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(setting => setting.Key == key, ct);
        if (existing is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else existing.Value = value;
    }
}
