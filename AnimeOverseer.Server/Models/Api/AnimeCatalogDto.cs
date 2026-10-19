namespace AnimeOverseer.Server.Models.Api;

/// <summary>A stable, navigation-free representation of an anime catalogue entry.</summary>
public sealed record AnimeCatalogDto
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool HasEnglishTitle { get; init; }
    public string? OriginalTitle { get; init; }
    public IReadOnlyList<string> AlternativeTitles { get; init; } = [];
    public string? Synopsis { get; init; }
    public string? ImageUrl { get; init; }
    public string? LocalImagePath { get; init; }
    public string? DisplayImageUrl { get; init; }
    public string? DisplayLocalImagePath { get; init; }
    public int? MALId { get; init; }
    public int? AniListId { get; init; }
    public int? KitsuId { get; init; }
    public string? Type { get; init; }
    public int? Episodes { get; init; }
    public int? Duration { get; init; }
    public decimal? Rating { get; init; }
    public string? Status { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public DateTime? CachedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }
    public int SeasonId { get; init; }
    public int? PreferredImageId { get; init; }
    public AnimeSeasonDto? Season { get; init; }
    public IReadOnlyList<AnimeTagDto> Genres { get; init; } = [];
    public IReadOnlyList<AnimeTagDto> Themes { get; init; } = [];
    public IReadOnlyList<AnimeTagDto> Demographics { get; init; } = [];
    public IReadOnlyList<AnimeImageDto> Images { get; init; } = [];
}

public sealed record AnimeSeasonDto(int Id, string Name, int Year);
public sealed record AnimeTagDto(int Id, string Name);
public sealed record AnimeImageDto(int Id, string Source, string Type, string ImageUrl, string? LocalImagePath);
