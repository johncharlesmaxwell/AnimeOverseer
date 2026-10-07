using System.ComponentModel.DataAnnotations;

namespace AnimeOverseer.Server.Models;

public enum CollectionType { Manual, Automatic }

/// <summary>A named, user-owned set of anime, either curated manually or driven by saved filters.</summary>
public class AnimeCollection
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    public CollectionType Type { get; set; } = CollectionType.Manual;

    // JSON representation of FilterState for automatic collections.
    public string? FilterJson { get; set; }

    public bool ShowOnHomepage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<AnimeCollectionItem> Items { get; set; } = new();
}

public class AnimeCollectionItem
{
    public int CollectionId { get; set; }
    public AnimeCollection Collection { get; set; } = null!;
    public int AnimeId { get; set; }
    public Anime Anime { get; set; } = null!;
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
