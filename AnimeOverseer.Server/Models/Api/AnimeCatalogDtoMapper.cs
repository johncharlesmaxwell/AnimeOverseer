using AnimeOverseer.Server.Models;

namespace AnimeOverseer.Server.Models.Api;

public static class AnimeCatalogDtoMapper
{
    public static AnimeCatalogDto ToCatalogDto(this Anime anime) => new()
    {
        Id = anime.Id,
        Title = anime.Title,
        HasEnglishTitle = anime.HasEnglishTitle,
        OriginalTitle = anime.OriginalTitle,
        AlternativeTitles = anime.TitleAliases.Select(alias => alias.Title).Distinct().ToArray(),
        Synopsis = anime.Synopsis,
        ImageUrl = anime.ImageUrl,
        LocalImagePath = anime.LocalImagePath,
        DisplayImageUrl = anime.DisplayImageUrl,
        DisplayLocalImagePath = anime.DisplayLocalImagePath,
        MALId = anime.MALId,
        AniListId = anime.AniListId,
        KitsuId = anime.KitsuId,
        Type = anime.Type,
        Episodes = anime.Episodes,
        Duration = anime.Duration,
        Rating = anime.Rating,
        Status = anime.Status,
        StartDate = anime.StartDate,
        EndDate = anime.EndDate,
        CachedAt = anime.CachedAt,
        CreatedAt = anime.CreatedAt,
        ModifiedAt = anime.ModifiedAt,
        SeasonId = anime.SeasonId,
        PreferredImageId = anime.PreferredImageId,
        Season = anime.Season is null ? null : new AnimeSeasonDto(anime.Season.Id, anime.Season.Name, anime.Season.Year),
        Genres = anime.AnimeGenres.Where(link => link.Genre is not null)
            .Select(link => new AnimeTagDto(link.Genre.Id, link.Genre.Name)).ToArray(),
        Themes = anime.AnimeThemes.Where(link => link.Theme is not null)
            .Select(link => new AnimeTagDto(link.Theme.Id, link.Theme.Name)).ToArray(),
        Demographics = anime.AnimeDemographics.Where(link => link.Demographic is not null)
            .Select(link => new AnimeTagDto(link.Demographic.Id, link.Demographic.Name)).ToArray(),
        Images = anime.Images.Select(image => new AnimeImageDto(
            image.Id, image.Source, image.Type, image.ImageUrl, image.LocalImagePath)).ToArray()
    };
}
