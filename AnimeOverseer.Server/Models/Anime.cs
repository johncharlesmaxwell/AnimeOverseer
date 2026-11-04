using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AnimeOverseer.Server.Models;

public class AnimeRelation
{
    public string RelationType { get; set; } = string.Empty; // Prequel, Sequel, Side Story, etc.
    public int AnimeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string? LocalImagePath { get; set; }
}

public class CachedAnimeRelation
{
    public int Id { get; set; }
    public int RootMalId { get; set; }
    public int RelatedMalId { get; set; }
    public string RelationType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
}

public class Anime
{
    public int Id { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    // Indicates that Title came from a source's explicitly localized English field.
    // Persisted so a later romaji-only sync cannot replace an English title.
    public bool HasEnglishTitle { get; set; }

    // Once a user chooses a primary English name, stop offering this anime's
    // remaining aliases in the name review queue.
    public bool NamesReviewed { get; set; }

    // Provider-supplied English, romaji, native, and synonym titles used while
    // resolving the catalogue identity. They are retained as AnimeTitleAlias
    // rows after import so aliases from one provider can match future imports.
    [NotMapped]
    public List<string> AlternativeTitles { get; set; } = new();

    public List<AnimeTitleAlias> TitleAliases { get; set; } = new();

    public string? OriginalTitle { get; set; }

    public string? Synopsis { get; set; }

    public string? ImageUrl { get; set; }

    public int? MALId { get; set; }

    public int? AniListId { get; set; }

    public int? KitsuId { get; set; }

    public string? Type { get; set; } // TV, Movie, OVA, Special, ONA, Music

    public int? Episodes { get; set; }

    public int? Duration { get; set; } // in minutes

    public decimal? Rating { get; set; }

    public string? Status { get; set; } // Currently Airing, Finished, Not Yet Aired

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public DateTime? CachedAt { get; set; }

    // Audit timestamps are maintained by AnimeDbContext whenever this catalogue
    // record is inserted or changed.
    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    public string? LocalImagePath { get; set; }

    // A user-selected artwork record to use in place of the catalogue's default cover.
    public int? PreferredImageId { get; set; }

    public int SeasonId { get; set; }
    public Season Season { get; set; } = null!;

    public List<AnimeGenre> AnimeGenres { get; set; } = new();

    public List<AnimeTheme> AnimeThemes { get; set; } = new();

    public List<AnimeDemographic> AnimeDemographics { get; set; } = new();

    public List<Request> Requests { get; set; } = new();

    public List<AnimeImage> Images { get; set; } = new();

    [NotMapped]
    public AnimeImage? PreferredImage => PreferredImageId is int imageId
        ? Images.FirstOrDefault(image => image.Id == imageId)
        : null;

    [NotMapped]
    public string? DisplayImageUrl => PreferredImage?.ImageUrl ?? ImageUrl;

    [NotMapped]
    public string? DisplayLocalImagePath => PreferredImage?.LocalImagePath ?? LocalImagePath;

    [NotMapped]
    public List<AnimeRelation> Relations { get; set; } = new();

    [NotMapped]
    public List<AnimeRelation> RelatedAnime { get; set; } = new();

    [NotMapped]
    public List<SourceImage> SourceImages { get; set; } = new();

    // Franchise entries a provider lists as related to this import (sequels,
    // prequels, side stories). A related entry is never the same anime.
    [NotMapped]
    public List<ProviderRelation> ProviderRelations { get; set; } = new();
}

/// <summary>A persisted AniList recommendation from one locally stored anime to another.</summary>
public class AnimeRecommendation
{
    public int SourceAnimeId { get; set; }
    public int RecommendedAnimeId { get; set; }
    public int Rating { get; set; }
    public DateTime CachedAt { get; set; }
}

public class AnimeImage
{
    public int Id { get; set; }
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = "Poster"; // Poster, Banner, Background
    public string ImageUrl { get; set; } = string.Empty;
    public string? LocalImagePath { get; set; }
}

public class AnimeTitleAlias
{
    public int Id { get; set; }
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// An import whose identity cannot be established safely.  The source record is
/// retained as a compact JSON snapshot until someone chooses to merge or create it.
/// </summary>
public class PendingAnimeReview
{
    public int Id { get; set; }
    public int? SyncJobId { get; set; }
    public int SeasonId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int? SourceAniListId { get; set; }
    public int? SourceMalId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string CandidateAnimeIdsJson { get; set; } = "[]";
    // The best candidate's 0-100 match score, and every candidate's scored
    // evidence. Null on reviews queued before match scoring existed.
    public int? MatchScore { get; set; }
    public string? MatchEvidenceJson { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Merged, Created, Deferred
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    // The catalogue row a Merge or Create produced.
    public int? ResolvedAnimeId { get; set; }
    // True when the decision was made without a person, so it can be undone.
    public bool AutoResolved { get; set; }
    // The merged row as it was before a Merge, for undo. See AnimeRestorePoint.
    public string? PreMergeSnapshotJson { get; set; }
    // Set when a person undoes an automatic decision; automation then leaves it alone.
    public bool AutoDecisionUndone { get; set; }
    // A Deferred review stays snoozed until this time; later imports of the same
    // source then reopen it. Null on reviews deferred before snoozing existed.
    public DateTime? DeferredUntil { get; set; }
}

/// <summary>
/// A person's answer to "is this provider record the same anime as this catalogue
/// row?", remembered so later imports are not asked the same question again.
/// </summary>
public class AnimeMatchDecision
{
    public int Id { get; set; }
    public int? SourceAniListId { get; set; }
    public int? SourceMalId { get; set; }
    public int AnimeId { get; set; }
    public bool IsSameAnime { get; set; }
    public string Origin { get; set; } = string.Empty; // Merge, Create, Undo
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;

    public bool AppliesTo(int? aniListId, int? malId) =>
        (SourceAniListId is > 0 && SourceAniListId == aniListId) || (SourceMalId is > 0 && SourceMalId == malId);
}

/// <summary>A merge target's own state before a review merged an import into it.</summary>
public class AnimeRestorePoint
{
    public AnimeImportSnapshot Fields { get; set; } = new();
    public string? LocalImagePath { get; set; }
    public int? PreferredImageId { get; set; }
    public List<string> Aliases { get; set; } = [];
    public List<int> ImageIds { get; set; } = [];
}

/// <summary>Serializable import fields needed to safely resume a reviewed decision.</summary>
public class AnimeImportSnapshot
{
    public int? AniListId { get; set; }
    public int? MALId { get; set; }
    public int? KitsuId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool HasEnglishTitle { get; set; }
    public string? OriginalTitle { get; set; }
    public List<string> AlternativeTitles { get; set; } = [];
    public string? Synopsis { get; set; }
    public string? ImageUrl { get; set; }
    public string? Type { get; set; }
    public int? Episodes { get; set; }
    public int? Duration { get; set; }
    public decimal? Rating { get; set; }
    public string? Status { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Themes { get; set; } = [];
    public List<string> Demographics { get; set; } = [];
    public List<SourceImage> SourceImages { get; set; } = [];
    public List<ProviderRelation> ProviderRelations { get; set; } = [];
}

/// <summary>A provider's link from an imported anime to another entry in its franchise.</summary>
public class ProviderRelation
{
    public string RelationType { get; set; } = string.Empty;
    public int? AniListId { get; set; }
    public int? MalId { get; set; }
}

public class SourceImage
{
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = "Poster";
    public string Url { get; set; } = string.Empty;
}

public class Genre
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public List<AnimeGenre> AnimeGenres { get; set; } = new();
}

public class AnimeGenre
{
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;

    public int GenreId { get; set; }
    public Genre Genre { get; set; } = null!;
}

public class Theme
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public List<AnimeTheme> AnimeThemes { get; set; } = new();
}

public class AnimeTheme
{
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;

    public int ThemeId { get; set; }
    public Theme Theme { get; set; } = null!;
}

public class Demographic
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public List<AnimeDemographic> AnimeDemographics { get; set; } = new();
}

public class AnimeDemographic
{
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;

    public int DemographicId { get; set; }
    public Demographic Demographic { get; set; } = null!;
}

public class Season
{
    public int Id { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public int Year { get; set; }

    public List<Anime> Animes { get; set; } = new();
}

public class Request
{
    public int Id { get; set; }

    [Required]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Completed

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public string? RequestedBy { get; set; }
}
