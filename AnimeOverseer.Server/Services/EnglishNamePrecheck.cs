using System.Globalization;
using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace AnimeOverseer.Server.Services;

/// <summary>How the English-name pre-check settles a record before it reaches name review.</summary>
public sealed record EnglishNameSettings
{
    public const string EnabledKey = "names.precheckEnabled";
    public const string MinSourcesKey = "names.precheckMinSources";
    public const string TieBreakKey = "names.precheckTieBreak";
    public const string PriorityKey = "names.sourcePriority";
    public const int MaximumMinSources = 4;

    public static readonly IReadOnlyList<string> KnownSources = ["AniList", "MyAnimeList", "Kitsu", "AnimeSchedule"];
    public static readonly EnglishNameSettings Default = new();

    /// <summary>Run the pre-check after each completed sync.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many sources must give the winning English name before it is applied.</summary>
    public int MinSources { get; init; } = 1;

    /// <summary>When sources disagree, pick the name most sources give, then the highest-priority source's.</summary>
    public bool TieBreak { get; init; }

    /// <summary>Sources in priority order, highest first. Also picks the spelling when sources agree.</summary>
    public IReadOnlyList<string> Priority { get; init; } = KnownSources;

    public int Rank(string source)
    {
        var index = Priority.ToList().FindIndex(item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public static EnglishNameSettings Read(IReadOnlyDictionary<string, string?> values)
    {
        var priority = (values.GetValueOrDefault(PriorityKey) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(source => KnownSources.Contains(source, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A source missing from a stored list keeps its default place at the end.
        priority.AddRange(KnownSources.Where(source => !priority.Contains(source, StringComparer.OrdinalIgnoreCase)));
        return new EnglishNameSettings
        {
            Enabled = !bool.TryParse(values.GetValueOrDefault(EnabledKey), out var enabled) || enabled,
            MinSources = int.TryParse(values.GetValueOrDefault(MinSourcesKey), NumberStyles.None, CultureInfo.InvariantCulture, out var min)
                ? Math.Clamp(min, 1, MaximumMinSources) : Default.MinSources,
            TieBreak = bool.TryParse(values.GetValueOrDefault(TieBreakKey), out var tieBreak) && tieBreak,
            Priority = priority
        };
    }

    public static async Task<EnglishNameSettings> LoadAsync(AnimeDbContext db, CancellationToken ct = default)
    {
        var values = await db.AppSettings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith("names.precheck") || setting.Key == PriorityKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);
        return Read(values);
    }

    public static async Task SaveAsync(AnimeDbContext db, EnglishNameSettings settings, CancellationToken ct = default)
    {
        var stored = Read(new Dictionary<string, string?>
        {
            [EnabledKey] = settings.Enabled.ToString(),
            [MinSourcesKey] = settings.MinSources.ToString(CultureInfo.InvariantCulture),
            [TieBreakKey] = settings.TieBreak.ToString(),
            [PriorityKey] = string.Join(',', settings.Priority)
        });
        await SetAsync(db, EnabledKey, stored.Enabled.ToString(), ct);
        await SetAsync(db, MinSourcesKey, stored.MinSources.ToString(CultureInfo.InvariantCulture), ct);
        await SetAsync(db, TieBreakKey, stored.TieBreak.ToString(), ct);
        await SetAsync(db, PriorityKey, string.Join(',', stored.Priority), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetAsync(AnimeDbContext db, string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FirstOrDefaultAsync(setting => setting.Key == key, ct);
        if (existing is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else existing.Value = value;
    }
}

/// <summary>One English name and the sources that give it.</summary>
public sealed record EnglishNameOption(string Title, IReadOnlyList<string> Sources);

public enum EnglishNameOutcome
{
    /// <summary>No source gives an English name.</summary>
    None,
    /// <summary>Every source that gives an English name gives the same one.</summary>
    Agreed,
    /// <summary>Sources disagree, and the tie-break picked one.</summary>
    TieBroken,
    /// <summary>Sources disagree, or too few give the name; a person decides.</summary>
    NeedsPerson
}

public sealed record EnglishNameVerdict(EnglishNameOutcome Outcome, string? Title, IReadOnlyList<EnglishNameOption> Options)
{
    public bool Applies => Outcome is EnglishNameOutcome.Agreed or EnglishNameOutcome.TieBroken;
}

/// <summary>
/// Decides a record's English name from the English titles its sources give. Names that
/// differ only in case, punctuation, accents or spacing count as the same name.
/// </summary>
public static class EnglishNamePrecheck
{
    public static EnglishNameVerdict Evaluate(IEnumerable<(string Source, string Title)> sourceTitles, EnglishNameSettings settings)
    {
        var options = sourceTitles
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Source))
            .Select(item => (item.Source, Title: item.Title.Trim(), Key: TitleNormalizer.Compact(item.Title)))
            .Where(item => item.Key.Length > 0)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => settings.Rank(item.Source)).ThenBy(item => item.Source, StringComparer.Ordinal).ToList();
                var sources = ordered.Select(item => item.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // The highest-priority source's spelling stands for the whole group.
                return new EnglishNameOption(ordered[0].Title, sources);
            })
            .OrderByDescending(option => option.Sources.Count)
            .ThenBy(option => option.Sources.Min(settings.Rank))
            .ToList();

        if (options.Count == 0) return new(EnglishNameOutcome.None, null, options);
        var best = options[0];
        if (best.Sources.Count < settings.MinSources) return new(EnglishNameOutcome.NeedsPerson, null, options);
        if (options.Count == 1) return new(EnglishNameOutcome.Agreed, best.Title, options);
        return settings.TieBreak
            ? new(EnglishNameOutcome.TieBroken, best.Title, options)
            : new(EnglishNameOutcome.NeedsPerson, null, options);
    }

    public static EnglishNameVerdict Evaluate(Anime anime, EnglishNameSettings settings)
        => Evaluate(anime.SourceTitles.Select(item => (item.Source, item.Title)), settings);
}

/// <summary>Keeps one row per source with the English title that source last gave.</summary>
public static class SourceTitleStore
{
    public static async Task RecordAsync(AnimeDbContext db, Anime target, Anime source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.DataSource) || string.IsNullOrWhiteSpace(source.SourceEnglishTitle)) return;
        var title = source.SourceEnglishTitle.Trim();

        // One provider page can resolve several entries to the same record before the
        // batch is saved, so look in the change tracker before the database.
        var row = db.ChangeTracker.Entries<AnimeSourceTitle>()
            .Where(entry => entry.State != EntityState.Deleted &&
                            (ReferenceEquals(entry.Entity.Anime, target) || (target.Id > 0 && entry.Entity.AnimeId == target.Id)) &&
                            entry.Entity.Source == source.DataSource)
            .Select(entry => entry.Entity)
            .FirstOrDefault();
        if (row is null && target.Id > 0)
            row = await db.AnimeSourceTitles.FirstOrDefaultAsync(item => item.AnimeId == target.Id && item.Source == source.DataSource, ct);

        if (row is null)
            db.AnimeSourceTitles.Add(new AnimeSourceTitle { Anime = target, Source = source.DataSource, Title = title });
        else if (!string.Equals(row.Title, title, StringComparison.Ordinal))
        {
            row.Title = title;
            row.UpdatedAt = DateTime.UtcNow;
        }
    }
}

/// <summary>What one pre-check run changed.</summary>
public sealed record EnglishNamePrecheckResult(int Renamed, int Confirmed, int ReturnedToReview)
{
    public int Total => Renamed + Confirmed;
}
