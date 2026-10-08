using System.Text.Json;
using AnimeOverseer.Server.Controllers;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Models.Api;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class AnimeControllerCatalogDtoTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Catalogue_endpoints_return_navigation_free_entries_with_aliases_and_catalogue_data()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = new Anime
        {
            Title = "Example Series",
            OriginalTitle = "Example Series Original",
            HasEnglishTitle = true,
            Synopsis = "A short synopsis.",
            ImageUrl = "https://example.test/default.jpg",
            LocalImagePath = "/images/default.jpg",
            PreferredImageId = 17,
            AniListId = 1234,
            MALId = 5678,
            KitsuId = 9876,
            Type = "TV",
            Episodes = 12,
            Duration = 24,
            Rating = 8.4m,
            Status = "Finished",
            StartDate = new DateTime(2026, 1, 1),
            Season = season
        };
        anime.TitleAliases.Add(new AnimeTitleAlias { Title = "Example Alias", Anime = anime });
        anime.Images.Add(new AnimeImage
        {
            Id = 17,
            Source = "AniList",
            Type = "Poster",
            ImageUrl = "https://example.test/preferred.jpg",
            LocalImagePath = "/images/preferred.jpg",
            Anime = anime
        });
        anime.AnimeGenres.Add(new AnimeGenre { Anime = anime, Genre = new Genre { Name = "Drama" } });
        anime.AnimeThemes.Add(new AnimeTheme { Anime = anime, Theme = new Theme { Name = "School" } });
        anime.AnimeDemographics.Add(new AnimeDemographic { Anime = anime, Demographic = new Demographic { Name = "Seinen" } });
        db.Animes.Add(anime);
        await db.SaveChangesAsync();

        var controller = new AnimeController(CreateCache(db));
        var seasonResult = Assert.IsType<OkObjectResult>(await controller.GetSeasonAnimes(2026, "spring"));
        var searchResult = Assert.IsType<OkObjectResult>(await controller.Search("Example"));
        var detailResult = Assert.IsType<OkObjectResult>(await controller.GetById(anime.Id));

        var seasonDto = Assert.Single(Assert.IsType<List<AnimeCatalogDto>>(seasonResult.Value));
        var searchDto = Assert.Single(Assert.IsType<List<AnimeCatalogDto>>(searchResult.Value));
        var detailDto = Assert.IsType<AnimeCatalogDto>(detailResult.Value);
        foreach (var dto in new[] { seasonDto, searchDto, detailDto })
        {
            Assert.Equal(anime.Id, dto.Id);
            Assert.Equal("Example Series", dto.Title);
            Assert.Contains("Example Alias", dto.AlternativeTitles);
            Assert.Equal("https://example.test/default.jpg", dto.ImageUrl);
            Assert.Equal("/images/default.jpg", dto.LocalImagePath);
            Assert.Equal("https://example.test/preferred.jpg", dto.DisplayImageUrl);
            Assert.Equal("/images/preferred.jpg", dto.DisplayLocalImagePath);
            Assert.Equal(("spring", 2026), (dto.Season?.Name, dto.Season?.Year));
            Assert.Equal("Drama", Assert.Single(dto.Genres).Name);
            Assert.Equal("School", Assert.Single(dto.Themes).Name);
            Assert.Equal("Seinen", Assert.Single(dto.Demographics).Name);
            Assert.Equal("https://example.test/preferred.jpg", Assert.Single(dto.Images).ImageUrl);

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal("Example Alias", root.GetProperty("alternativeTitles")[0].GetString());
            Assert.False(root.TryGetProperty("titleAliases", out _));
            Assert.False(root.TryGetProperty("requests", out _));
            Assert.False(root.GetProperty("images")[0].TryGetProperty("anime", out _));
            Assert.False(root.GetProperty("genres")[0].TryGetProperty("animeGenres", out _));
        }
    }

    [Fact]
    public async Task Detail_endpoint_preserves_not_found_behavior()
    {
        await using var db = CreateDb();
        var controller = new AnimeController(CreateCache(db));

        Assert.IsType<NotFoundResult>(await controller.GetById(404));
    }
}
