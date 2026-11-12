namespace AnimeOverseer.Server.Services;

/// <summary>Remembered page sizes for the review lists. Each list keeps its own choice.</summary>
public static class ReviewPagingSettings
{
    public const string MatchesKey = "review.matches.pageSize";
    public const string NamesKey = "review.names.pageSize";
    public const int DefaultPageSize = 24;
    public static readonly int[] PageSizes = [12, 24, 48, 96];
    public static int Read(string? value) =>
        int.TryParse(value, out var size) && PageSizes.Contains(size) ? size : DefaultPageSize;
}
