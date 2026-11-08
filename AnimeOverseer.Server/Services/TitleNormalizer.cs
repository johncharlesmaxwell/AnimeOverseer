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
    private static readonly HashSet<string> StopTokens = new(StringComparer.Ordinal) { "the", "a", "an", "of", "season", "part", "cour" };

    /// <summary>
    /// Lowercase, accent-free words separated by single spaces, with full-width and other
    /// compatibility characters folded to their plain forms and digits as ASCII. Apostrophes are dropped.
    /// </summary>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var result = new StringBuilder(title.Length);
        var previousWasSeparator = true;
        foreach (var character in title.Trim().Normalize(NormalizationForm.FormKD))
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
            // FormKD folds full-width forms ("４", "Ｓ"); any other decimal digit becomes ASCII
            // so numbers in labels always parse.
            if (char.IsDigit(character))
            {
                result.Append((char)('0' + (int)char.GetNumericValue(character)));
                previousWasSeparator = false;
            }
            else if (char.IsLetter(character))
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

    /// <summary>
    /// Meaningful tokens for fuzzy comparison: stop words removed, roman numerals as digits,
    /// and season labels in one form, so "2nd Season", "Season 2" and a trailing "II" agree.
    /// </summary>
    public static HashSet<string> Tokens(string? title)
        => SeasonCanonical(title).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(token => !StopTokens.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Jaccard similarity of two token sets.</summary>
    public static double TokenScore(HashSet<string> left, HashSet<string> right)
    {
        // Counted without building the union and intersection; this runs per candidate pair.
        var (small, large) = left.Count <= right.Count ? (left, right) : (right, left);
        var shared = 0;
        foreach (var token in small)
            if (large.Contains(token)) shared++;
        var union = left.Count + right.Count - shared;
        return union == 0 ? 0 : (double)shared / union;
    }

    /// <summary>
    /// The season a title names, or 1 when it names none. See <see cref="InstallmentOf(string?)"/>;
    /// a final season with no number is reported as 0.
    /// </summary>
    public static int SeasonNumber(string? title) => InstallmentOf(title) is { FinalSeason: true } ? 0 : InstallmentOf(title).Season;

    /// <summary>
    /// The season and part a title names. Recognised forms: "Season 2", "Season II", "2nd Season",
    /// "Second Season", "S2" (at the end), "Final Season", "Part 2", "Cour 2", "2nd Cour",
    /// "Second Part", a leading "Zoku", "Shippuden", the native 第2期 and 第2クール, and a trailing
    /// "II" or small number. Large trailing numbers such as "Mob Psycho 100" are part of the name.
    /// </summary>
    public static Installment InstallmentOf(string? title) => ParseLabel(title).ToInstallment();

    /// <summary>
    /// The season and part named by either title, preferring the main title. Romaji titles often
    /// carry "2nd Season" when the English title has only a subtitle.
    /// </summary>
    public static Installment InstallmentOf(string? mainTitle, string? originalTitle)
    {
        var main = ParseLabel(mainTitle);
        var original = ParseLabel(originalTitle);
        var season = main.HasSeason ? main : original;
        return new ParsedLabel(season.Season, season.Final, main.Part ?? original.Part).ToInstallment();
    }

    /// <summary>
    /// The normalized title with season labels in one form ("s2", "p2", "sfinal") and labels
    /// for season 1 or part 1 dropped, so "Frieren 2nd Season" and "Frieren S2" read
    /// "frieren s2" and "Frieren Season 1" reads "frieren".
    /// </summary>
    public static string SeasonCanonical(string? title)
    {
        var tokens = Words(title);
        if (tokens.Length == 0) return string.Empty;
        var spans = FindLabels(tokens);
        var parsed = ParseLabel(tokens, spans);
        var words = new List<string>(tokens.Length + 1);
        var zoku = !parsed.HasExplicitSeason(spans) && tokens.Length >= 2 && tokens[0] == "zoku";
        var bareStart = BareTrailingNumber(tokens, spans) is not null && !parsed.HasExplicitSeason(spans) && !zoku ? tokens.Length - 1 : -1;
        for (var i = zoku ? 1 : 0; i < tokens.Length; i++)
        {
            if (i == bareStart) { words.Add(SeasonToken(parsed.Season ?? 1)); continue; }
            var span = spans.FirstOrDefault(candidate => candidate.Start == i);
            if (span.End == 0) { words.Add(tokens[i]); continue; }
            if (span.Kind == LabelKind.FinalSeason && words.Count > 0 && words[^1] == "the") words.RemoveAt(words.Count - 1);
            var token = span.Kind switch
            {
                LabelKind.FinalSeason => "sfinal",
                LabelKind.Season => span.Value == 1 ? null : SeasonToken(span.Value),
                _ => span.Value == 1 ? null : "p" + span.Value.ToString(CultureInfo.InvariantCulture)
            };
            if (token is not null) words.Add(token);
            i = span.End - 1;
        }
        if (zoku) words.Add("s2");
        return string.Join(' ', words);
    }

    /// <summary>
    /// Splits a trailing explicit season label off a title: "Frieren 2nd Season" gives
    /// ("frieren", Season 2) and "Attack on Titan: The Final Season" gives ("attack on titan",
    /// Final Season). A bare trailing number is not a label here, since "Title 2" is commonly
    /// a distinct sequel. The label is null when there is none.
    /// </summary>
    public static (string Base, Installment? Label) SplitSeasonSuffix(string? title)
    {
        var tokens = Words(title);
        var spans = FindLabels(tokens);
        var start = tokens.Length;
        for (var index = spans.Count - 1; index >= 0 && spans[index].End == start; index--)
            start = spans[index].Start;
        if (start == tokens.Length) return (string.Join(' ', tokens), null);
        var run = spans.Where(span => span.Start >= start).ToList();
        if (start > 0 && tokens[start - 1] == "the" && run.Any(span => span.Kind == LabelKind.FinalSeason)) start--;
        var label = ParseLabel(tokens[start..], run.Select(span => span with { Start = span.Start - start, End = span.End - start }).ToList());
        return (string.Join(' ', tokens[..start]), label.ToInstallment());
    }

    /// <summary>True when <paramref name="text"/> is only an explicit season label.</summary>
    public static bool IsSeasonIndicator(string text) => SplitSeasonSuffix(text) is { Base.Length: 0, Label: not null };

    private enum LabelKind { Season, FinalSeason, Part }

    private readonly record struct LabelSpan(int Start, int End, LabelKind Kind, int Value);

    private readonly record struct ParsedLabel(int? Season, bool Final, int? Part)
    {
        public bool HasSeason => Season is not null || Final;
        public bool HasExplicitSeason(List<LabelSpan> spans) => spans.Any(span => span.Kind != LabelKind.Part);
        public Installment ToInstallment() => new(Final ? 0 : Season ?? 1, Part ?? 1, Final);
    }

    private static string[] Words(string? title) => Normalize(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static ParsedLabel ParseLabel(string? title)
    {
        var tokens = Words(title);
        return tokens.Length == 0 ? default : ParseLabel(tokens, FindLabels(tokens));
    }

    private static ParsedLabel ParseLabel(string[] tokens, List<LabelSpan> spans)
    {
        int? season = null, part = null;
        var final = false;
        foreach (var span in spans)
        {
            if (span.Kind == LabelKind.Part) part ??= span.Value;
            else if (season is null && !final)
            {
                if (span.Kind == LabelKind.FinalSeason) final = true;
                else season = span.Value;
            }
        }
        if (season is null && !final)
        {
            if (tokens.Length >= 2 && tokens[0] == "zoku") season = 2;
            else if (tokens.Any(token => token is "shippuden" or "shippuuden")) season = 2;
            else season = BareTrailingNumber(tokens, spans);
        }
        return new ParsedLabel(season, final, part);
    }

    private static int? BareTrailingNumber(string[] tokens, List<LabelSpan> spans)
    {
        if (tokens.Length < 2 || spans.Any(span => span.End == tokens.Length)) return null;
        return int.TryParse(NormalizeToken(tokens[^1]), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 2 and <= 20
            ? number
            : null;
    }

    private static List<LabelSpan> FindLabels(string[] tokens)
    {
        var spans = new List<LabelSpan>();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (MatchLabel(tokens, index) is not { } span) continue;
            spans.Add(span);
            index = span.End - 1;
        }
        return spans;
    }

    private static LabelSpan? MatchLabel(string[] tokens, int index)
    {
        var token = tokens[index];
        var next = index + 1 < tokens.Length ? tokens[index + 1] : null;
        // "Season 2", "Season II", "Part 2", "Cour 2"
        if (token is "season" or "part" or "cour" && next is not null && LabelNumber(next) is int number)
            return new LabelSpan(index, index + 2, KindOf(token), number);
        // "Season2", "Part2"
        var joined = JoinedLabel.Match(token);
        if (joined.Success)
            return new LabelSpan(index, index + 1, KindOf(joined.Groups[1].Value), int.Parse(joined.Groups[2].Value, CultureInfo.InvariantCulture));
        // "2nd Season", "Second Cour"
        if (next is "season" or "part" or "cour" && Ordinal(token) is int ordinal)
            return new LabelSpan(index, index + 2, KindOf(next), ordinal);
        if (token == "final" && next == "season")
            return new LabelSpan(index, index + 2, LabelKind.FinalSeason, 0);
        // "S2" and "S 2" only at the end, where the S cannot be an initial.
        var shortSeason = ShortSeason.Match(token);
        if (shortSeason.Success && index == tokens.Length - 1)
            return new LabelSpan(index, index + 1, LabelKind.Season, int.Parse(shortSeason.Groups[1].Value, CultureInfo.InvariantCulture));
        if (token == "s" && index == tokens.Length - 2 && Digits(next!) is int shortNumber)
            return new LabelSpan(index, index + 2, LabelKind.Season, shortNumber);
        // Native titles: 第2期 (season 2), 第2クール (part 2).
        var native = NativeLabel.Match(token);
        if (native.Success)
            return new LabelSpan(index, index + 1, native.Groups[2].Value == "期" ? LabelKind.Season : LabelKind.Part,
                int.Parse(native.Groups[1].Value, CultureInfo.InvariantCulture));
        return null;
    }

    private static LabelKind KindOf(string word) => word == "season" ? LabelKind.Season : LabelKind.Part;

    private static string SeasonToken(int season) => "s" + season.ToString(CultureInfo.InvariantCulture);

    private static int? LabelNumber(string token)
        => Digits(token) ?? (int.TryParse(NormalizeToken(token), NumberStyles.None, CultureInfo.InvariantCulture, out var roman) && token.All(char.IsLetter) ? roman : null);

    private static int? Digits(string token)
        => token.Length <= 2 && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private static int? Ordinal(string token)
    {
        var numeric = NumericOrdinal.Match(token);
        if (numeric.Success) return int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture);
        var index = Array.IndexOf(OrdinalWords, token);
        return index >= 0 ? index + 1 : null;
    }

    private static readonly string[] OrdinalWords = ["first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];
    private static readonly Regex JoinedLabel = new(@"^(season|part|cour)([0-9]{1,2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NumericOrdinal = new(@"^([0-9]{1,2})(?:st|nd|rd|th)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShortSeason = new(@"^s([0-9]{1,2})$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NativeLabel = new(@"^第?([0-9]{1,2})(期|クール)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string NormalizeToken(string token) => token switch
    {
        "i" => "1", "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5",
        "vi" => "6", "vii" => "7", "viii" => "8", "ix" => "9", "x" => "10",
        _ => token
    };
}

/// <summary>
/// The season and part a title names. A final season with no number has
/// <see cref="FinalSeason"/> set and a <see cref="Season"/> of 0.
/// </summary>
public readonly record struct Installment(int Season, int Part, bool FinalSeason = false)
{
    public static readonly Installment First = new(1, 1);

    /// <summary>
    /// True when the two name different seasons or parts. A final season only conflicts with
    /// season 1, since "The Final Season" is often "Season 4" under another provider's name.
    /// </summary>
    public bool ConflictsWith(Installment other)
    {
        if (Part != other.Part) return true;
        if (FinalSeason || other.FinalSeason)
            return FinalSeason != other.FinalSeason && (FinalSeason ? other.Season : Season) == 1;
        return Season != other.Season;
    }

    public override string ToString()
    {
        var season = FinalSeason ? "Final Season" : $"Season {Season}";
        return Part > 1 ? $"{season} Part {Part}" : season;
    }
}
