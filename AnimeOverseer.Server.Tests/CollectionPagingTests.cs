using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AnimeOverseer.Server.Tests;

public sealed class CollectionPagingTests : SqliteIntegrationTestBase
{
    [Theory]
    [InlineData(CollectionType.Manual, false)]
    [InlineData(CollectionType.Automatic, false)]
    [InlineData(CollectionType.Automatic, true)]
    public async Task Pages_have_stable_order_and_clamp_after_removal(CollectionType type, bool dynamicFilter)
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = Enumerable.Range(1, 25).Select(i => new Anime { Title = "Same title", SeasonId = season.Id }).ToList();
        db.Animes.AddRange(anime);
        await db.SaveChangesAsync();
        var service = new CollectionService(db);
        var filters = dynamicFilter ? new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition { Field = FilterField.Title, Value = "{{title}}" }] }]
        } : new FilterState();
        var collection = await service.CreateAsync("Paged", null, type, filters);
        if (type == CollectionType.Manual)
            foreach (var item in anime) await service.AddAnimeAsync(collection.Id, item.Id);

        var first = await service.GetAnimePageAsync(collection, 1, 12);
        var second = await service.GetAnimePageAsync(collection, 2, 12);
        var last = await service.GetAnimePageAsync(collection, 99, 12);
        Assert.Equal(25, first.TotalCount);
        Assert.Equal(anime.Take(12).Select(a => a.Id), first.Items.Select(a => a.Id));
        Assert.Equal(anime.Skip(12).Take(12).Select(a => a.Id), second.Items.Select(a => a.Id));
        Assert.Equal(3, last.Page);
        Assert.Equal(anime.Last().Id, Assert.Single(last.Items).Id);

        db.Animes.Remove(anime.Last());
        await db.SaveChangesAsync();
        var afterRemoval = await service.GetAnimePageAsync(collection, 3, 12);
        Assert.Equal(2, afterRemoval.Page);
        Assert.Equal(24, afterRemoval.TotalCount);
        Assert.Equal(12, afterRemoval.Items.Count);
    }

    [Fact]
    public async Task Empty_collection_has_one_empty_page()
    {
        await using var db = CreateDb();
        var service = new CollectionService(db);
        var collection = await service.CreateAsync("Empty", null, CollectionType.Manual, null);
        var result = await service.GetAnimePageAsync(collection, -1, 24);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(1, result.Page);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetAnimePageAsync(collection, 1, 0));
    }

    [Fact]
    public async Task Cached_search_combines_text_aliases_and_filters()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.AddRange(
            new Anime { Title = "Goblin Slayer", Type = "TV", SeasonId = season.Id },
            new Anime { Title = "Localized title", Type = "TV", SeasonId = season.Id, TitleAliases = [new() { Title = "Goblin Hero" }] },
            new Anime { Title = "Goblin Movie", Type = "Movie", SeasonId = season.Id },
            new Anime { Title = "Unrelated", Type = "TV", SeasonId = season.Id });
        await db.SaveChangesAsync();
        var service = new CollectionService(db);
        var filters = new FilterState
        {
            Groups = [new FilterGroup { Conditions = [new FilterCondition { Field = FilterField.Type, Operator = FilterOperator.Equals, Value = "TV" }] }]
        };

        var results = await service.SearchAnimeAsync("goblin", filters, 200);

        Assert.Equal(["Goblin Slayer", "Localized title"], results.Select(anime => anime.Title));
    }

    [Fact]
    public async Task Homepage_collections_can_be_selected_and_loaded_in_slices()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var anime = Enumerable.Range(1, 3)
            .Select(i => new Anime { Title = $"Anime {i}", SeasonId = season.Id })
            .ToList();
        db.Animes.AddRange(anime);
        await db.SaveChangesAsync();
        var service = new CollectionService(db);
        var collection = await service.CreateAsync("Homepage", null, CollectionType.Manual, null);
        foreach (var item in anime) await service.AddAnimeAsync(collection.Id, item.Id);

        Assert.Empty(await service.GetHomepageAsync());
        Assert.True(await service.SetShowOnHomepageAsync(collection.Id, true));
        Assert.Equal(collection.Id, Assert.Single(await service.GetHomepageAsync()).Id);

        var first = await service.GetAnimeSliceAsync(collection, 0, 2);
        var second = await service.GetAnimeSliceAsync(collection, 2, 2);
        Assert.Equal(3, first.TotalCount);
        Assert.Equal(anime.Take(2).Select(item => item.Id), first.Items.Select(item => item.Id));
        Assert.Equal(anime.Last().Id, Assert.Single(second.Items).Id);

        Assert.True(await service.SetShowOnHomepageAsync(collection.Id, false));
        Assert.Empty(await service.GetHomepageAsync());
    }

    [Fact]
    public async Task Preferences_persist_independently_and_can_return_to_default()
    {
        await using (var db = CreateDb())
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var settings = new SettingsService(db, cache);
            await settings.SetAsync(CollectionPagingSettings.DefaultKey, "48");
            await settings.SetAsync(CollectionPagingSettings.OverrideKey(1), "12");
        }
        await using var reloaded = CreateDb();
        using var freshCache = new MemoryCache(new MemoryCacheOptions());
        var saved = new SettingsService(reloaded, freshCache);
        Assert.Equal(48, CollectionPagingSettings.Read(await saved.GetAsync(CollectionPagingSettings.DefaultKey)));
        Assert.Equal(12, CollectionPagingSettings.Read(await saved.GetAsync(CollectionPagingSettings.OverrideKey(1))));
        Assert.Null(CollectionPagingSettings.Read(await saved.GetAsync(CollectionPagingSettings.OverrideKey(2))));
        await saved.SetAsync(CollectionPagingSettings.OverrideKey(1), null);
        Assert.Null(CollectionPagingSettings.Read(await saved.GetAsync(CollectionPagingSettings.OverrideKey(1))));
        Assert.Null(CollectionPagingSettings.Read("0"));
        Assert.Null(CollectionPagingSettings.Read("invalid"));
    }
}
