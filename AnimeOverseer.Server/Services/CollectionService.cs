using AnimeOverseer.Server.Data;
using AnimeOverseer.Server.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using AnimeOverseer.Server.Services.Expressions;

namespace AnimeOverseer.Server.Services;

public class CollectionService(AnimeDbContext db)
{
    private IQueryable<Anime> Animes() => db.Animes.AsNoTracking()
        .Include(a => a.Season).Include(a => a.Images).Include(a => a.TitleAliases)
        .Include(a => a.AnimeGenres).ThenInclude(link => link.Genre)
        .Include(a => a.AnimeThemes).ThenInclude(link => link.Theme)
        .Include(a => a.AnimeDemographics).ThenInclude(link => link.Demographic);

    public Task<List<AnimeCollection>> GetAllAsync() => db.AnimeCollections.AsNoTracking()
        .Include(collection => collection.Items)
        .OrderBy(collection => collection.Name).ToListAsync();

    public Task<List<AnimeCollection>> GetHomepageAsync() => db.AnimeCollections.AsNoTracking()
        .Where(collection => collection.ShowOnHomepage)
        .OrderBy(collection => collection.Name).ToListAsync();

    public Task<AnimeCollection?> GetAsync(int id) => db.AnimeCollections.AsNoTracking()
        .Include(collection => collection.Items)
        .FirstOrDefaultAsync(collection => collection.Id == id);

    public async Task<AnimeCollection> CreateAsync(string name, string? description, CollectionType type, FilterState? filters)
    {
        filters ??= new FilterState();
        if (type == CollectionType.Automatic) ValidateAndVersion(filters);
        var collection = new AnimeCollection { Name = name.Trim(), Description = description?.Trim(), Type = type, FilterJson = type == CollectionType.Automatic ? JsonSerializer.Serialize(filters) : null };
        db.AnimeCollections.Add(collection);
        await db.SaveChangesAsync();
        return collection;
    }

    public async Task<bool> UpdateAsync(int id, string name, string? description, CollectionType type, FilterState? filters)
    {
        var collection = await db.AnimeCollections.FindAsync(id);
        if (collection is null) return false;
        filters ??= new FilterState();
        if (type == CollectionType.Automatic) ValidateAndVersion(filters);
        collection.Name = name.Trim(); collection.Description = description?.Trim(); collection.Type = type;
        collection.FilterJson = type == CollectionType.Automatic ? JsonSerializer.Serialize(filters) : null;
        collection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SetShowOnHomepageAsync(int id, bool showOnHomepage)
    {
        var collection = await db.AnimeCollections.FindAsync(id);
        if (collection is null) return false;
        collection.ShowOnHomepage = showOnHomepage;
        collection.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task DeleteAsync(int id)
    {
        var collection = await db.AnimeCollections.FindAsync(id);
        if (collection is null) return;
        db.AnimeCollections.Remove(collection);
        await db.SaveChangesAsync();
    }

    public async Task<List<Anime>> GetAnimeAsync(AnimeCollection collection)
    {
        if (collection.Type == CollectionType.Manual)
            return await Animes().Where(anime => db.AnimeCollectionItems.Any(item => item.CollectionId == collection.Id && item.AnimeId == anime.Id)).OrderBy(anime => anime.Title).ToListAsync();
        var savedFilters = Deserialize(collection.FilterJson);
        if (CollectionFilterExpressionResolver.RequiresAnimeContext(savedFilters))
        {
            var animes = await Animes().ToListAsync();
            return animes.Where(anime => FilterEngine.Apply([anime], CollectionFilterExpressionResolver.Resolve(savedFilters, anime: anime)).Count == 1)
                .OrderByDescending(anime => anime.StartDate).ToList();
        }
        return await FilterQueryBuilder.Apply(Animes(), CollectionFilterExpressionResolver.Resolve(savedFilters)).OrderByDescending(anime => anime.StartDate).ToListAsync();
    }

    public async Task<(List<Anime> Items, int TotalCount, int Page)> GetAnimePageAsync(
        AnimeCollection collection, int page, int pageSize)
    {
        if (!CollectionPagingSettings.PageSizes.Contains(pageSize))
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var filters = Deserialize(collection.FilterJson);
        // Expressions referencing each anime must be evaluated before counting and paging.
        if (collection.Type == CollectionType.Automatic && CollectionFilterExpressionResolver.RequiresAnimeContext(filters))
        {
            var matches = await GetAnimeAsync(collection);
            page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(matches.Count / (double)pageSize)));
            return (matches.OrderByDescending(a => a.StartDate).ThenBy(a => a.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToList(), matches.Count, page);
        }

        var query = collection.Type == CollectionType.Manual
            ? Animes().Where(anime => db.AnimeCollectionItems.Any(item => item.CollectionId == collection.Id && item.AnimeId == anime.Id))
            : FilterQueryBuilder.Apply(Animes(), CollectionFilterExpressionResolver.Resolve(filters));
        var total = await query.CountAsync();
        page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var ordered = collection.Type == CollectionType.Manual
            ? query.OrderBy(a => a.Title).ThenBy(a => a.Id)
            : query.OrderByDescending(a => a.StartDate).ThenBy(a => a.Id);
        return (await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(), total, page);
    }

    public async Task<(List<Anime> Items, int TotalCount)> GetAnimeSliceAsync(
        AnimeCollection collection, int skip, int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var filters = Deserialize(collection.FilterJson);
        if (collection.Type == CollectionType.Automatic && CollectionFilterExpressionResolver.RequiresAnimeContext(filters))
        {
            var matches = await GetAnimeAsync(collection);
            return (matches.OrderByDescending(anime => anime.StartDate).ThenBy(anime => anime.Id)
                .Skip(skip).Take(take).ToList(), matches.Count);
        }

        var query = collection.Type == CollectionType.Manual
            ? Animes().Where(anime => db.AnimeCollectionItems.Any(item => item.CollectionId == collection.Id && item.AnimeId == anime.Id))
            : FilterQueryBuilder.Apply(Animes(), CollectionFilterExpressionResolver.Resolve(filters));
        var total = await query.CountAsync();
        var ordered = collection.Type == CollectionType.Manual
            ? query.OrderBy(anime => anime.Title).ThenBy(anime => anime.Id)
            : query.OrderByDescending(anime => anime.StartDate).ThenBy(anime => anime.Id);
        return (await ordered.Skip(skip).Take(take).ToListAsync(), total);
    }

    public async Task<bool> AddAnimeAsync(int collectionId, int animeId)
    {
        if (!await db.AnimeCollections.AnyAsync(collection => collection.Id == collectionId && collection.Type == CollectionType.Manual) || !await db.Animes.AnyAsync(anime => anime.Id == animeId)) return false;
        if (await db.AnimeCollectionItems.AnyAsync(item => item.CollectionId == collectionId && item.AnimeId == animeId)) return true;
        db.AnimeCollectionItems.Add(new AnimeCollectionItem { CollectionId = collectionId, AnimeId = animeId });
        await db.SaveChangesAsync(); return true;
    }

    public async Task RemoveAnimeAsync(int collectionId, int animeId)
    {
        var item = await db.AnimeCollectionItems.FindAsync(collectionId, animeId);
        if (item is null) return;
        db.AnimeCollectionItems.Remove(item); await db.SaveChangesAsync();
    }

    public async Task<List<Anime>> SearchAnimeAsync(string query, FilterState? filters = null, int take = 12)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        query = query.Trim();
        if (query.Length == 0) return [];
        var normalizedQuery = query.ToLower();

        return await FilterQueryBuilder.Apply(Animes(), filters ?? new FilterState())
            .Where(anime => anime.Title.ToLower().Contains(normalizedQuery)
                || (anime.OriginalTitle != null && anime.OriginalTitle.ToLower().Contains(normalizedQuery))
                || anime.TitleAliases.Any(alias => alias.Title.ToLower().Contains(normalizedQuery)))
            .OrderBy(anime => anime.Title)
            .ThenBy(anime => anime.Id)
            .Take(take)
            .ToListAsync();
    }

    public static FilterState Deserialize(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new FilterState() : JsonSerializer.Deserialize<FilterState>(json) ?? new FilterState(); }
        catch { return new FilterState(); }
    }

    private static void ValidateAndVersion(FilterState filters)
    {
        var errors = FilterExpressionValidator.Validate(filters);
        if (errors.Count > 0) throw new ArgumentException(errors[0], nameof(filters));
        filters.SchemaVersion = 2;
    }
}
