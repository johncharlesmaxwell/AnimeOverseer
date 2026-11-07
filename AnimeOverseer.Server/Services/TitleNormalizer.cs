using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeOverseer.Server.Services;

/// <summary>
/// Title normalization shared by catalogue identity matching and media-library
/// lookups, so both agree on what counts as "the same words".
/// </summary>
public static class TitleNormalizer
{
    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal) { "the", "a", "an", "of", "season", "part" };

    /// <summary>Lowercase, accent-free words separated by single spaces. Apostrophes are dropped.</summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var result = new StringBuilder(title.Length);
        var previousWasSeparator = true;
        foreach (var character in title.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            // Apostrophes are commonly omitted by provider and Sonarr/Radarr title sources.
            if (character is '\'' or '‘' or '’') continue;
            // "Hunter × Hunter" and "Hunter x Hunter", "A & B" and "A and B" are the same title.
            if (character is '×' or '&')
            {
                if (!previousWasSeparator) result.Append(' ');
                result.Append(character == '×' ? "x " : "and ");
                previousWasSeparator = true;
                continue;
            }
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                result.Append(' ');
                previousWasSeparator = true;
            }
        }
        return result.ToString().TrimEnd();
    }

    /// <summary>The normalized title with word boundaries removed, for exact-identity lookups.</summary>
    public static string Compact(string? title) => Normalize(title).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// A compact key with romaji long vowels folded, so "Yuuki", "Yūki" and "Yuki",
    /// or "Toukyou" and "Tokyo", agree. Macrons are already removed by <see cref="Normalize"/>.
    /// Looser than <see cref="Compact"/>: an English pair like "four"/"for" also agrees.
    /// </summary>
    public static string RomajiKey(string? title) => LongVowels.Replace(Compact(title), match => match.Value[..1]);

    private static readonly Regex LongVowels = new("o[ou]|uu|aa|ii|ee", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Folds provider format names to one vocabulary: "TV_SHORT", "tv short" and "TV" all become "TV".</summary>
    public static string? Format(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        var text = Regex.Replace(format.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"\s+", " ");
        return text switch
        {
            "tv" or "tv short" or "tv series" or "series" => "TV",
            "movie" or "film" => "Movie",
            "special" or "tv special" or "specials" => "Special",
            "ova" or "oad" => "OVA",
            "ona" or "web" => "ONA",
            "music" or "music video" => "Music",
            _ => format.Trim()
        };
    }

    /// <summary>True for a status meaning the show has not finished airing, so its episode count may still grow.</summary>
    public static bool IsUnfinished(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        var text = status.Trim().ToLowerInvariant().Replace('_', ' ');
        return text.Contains("currently") || text is "current" or "ongoing" or "releasing" or "airing" or "upcoming"
            or "not yet aired" or "not yet released" or "tba" or "unreleased" or "hiatus";
    }

    /// <summary>Meaningful tokens for fuzzy comparison: stop words removed, roman numerals as digits.</summary>
    public static HashSet<string> Tokens(string? title)
        => Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(token => !StopTokens.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Jaccard similarity of two token sets.</summary>
    public static double TokenScore(HashSet<string> left, HashSet<string> right)
    {
        var union = left.Union(right).Count();
        return union == 0 ? 0 : (double)left.Intersect(right).Count() / union;
    }

    /// <summary>
    /// The season a title explicitly names ("Season 2", "2nd Season", "S2", a trailing
    /// "II" or small number), or 1 when it names none. Large trailing numbers such as
    /// "Mob Psycho 100" are part of the name rather than a season.
    /// </summary>
    public static int SeasonNumber(string? title)
    {
        var text = Normalize(title);
        if (text.Length == 0) return 1;
        var explicitSeason = Regex.Match(text, @"(?:\bseason\s*(\d+)\b|\b(\d+)(?:st|nd|rd|th)\s+season\b|\bs\s*(\d+)$)", RegexOptions.CultureInvariant);
        if (explicitSeason.Success)
            return int.Parse(explicitSeason.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value, CultureInfo.InvariantCulture);
        var tokens = text.Split(' ');
        if (tokens.Length < 2) return 1;
        var last = NormalizeToken(tokens[^1]);
        return int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 2 and <= 20 ? number : 1;
    }

    /// <summary>True when <paramref name="text"/> is only an explicit season label.</summary>
    public static bool IsSeasonIndicator(string text)
        // Deliberately exclude a bare number ("Title 2"): it commonly denotes
        // a distinct sequel, so treating it as the same library item is unsafe.
        => Regex.IsMatch(text, @"^(?:season\s*\d+|\d+(?:st|nd|rd|th)\s+season|s\s*\d+)$", RegexOptions.CultureInvariant);

    private static string NormalizeToken(string token) => token switch
    {
        "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
        "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
        _ => token
    };
}
