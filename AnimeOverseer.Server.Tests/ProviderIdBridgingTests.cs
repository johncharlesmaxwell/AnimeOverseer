using System.Net;
using System.Text;
using System.Text.Json;
using AnimeOverseer.Server.Models;
using AnimeOverseer.Server.Services;
using AnimeOverseer.Server.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AnimeOverseer.Server.Tests;

[Collection(AniListRateGateCollection.Name)]
public sealed class ProviderIdBridgingTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Title_match_with_a_conflicting_anilist_id_is_created_without_review()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { AniListId = 81, Title = "Same Title", SeasonId = season.Id });
        await db.SaveChangesAsync();

        await ImportAsync(db, Incoming(82, "Same Title", false));

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(2, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Mal_only_import_merges_when_anilist_confirms_the_candidates_id()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var existing = new Anime { AniListId = 900, Title = "Orbit", SeasonId = season.Id };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        var aniList = new AniListIdsHandler(new ProviderIds(900, 500));

        await ImportAsync(db, MalOnly(500, "Orbit"), aniList);

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        var merged = await db.Animes.SingleAsync();
        Assert.Equal(existing.Id, merged.Id);
        Assert.Equal(500, merged.MALId);
        Assert.Contains("idMal", Assert.Single(aniList.Requests));
    }

    [Fact]
    public async Task Mal_only_import_is_created_when_anilist_maps_it_to_a_different_anime()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { AniListId = 900, Title = "Orbit", SeasonId = season.Id });
        await db.SaveChangesAsync();

        await ImportAsync(db, MalOnly(500, "Orbit"), new AniListIdsHandler(new ProviderIds(901, 500)));

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(2, await db.Animes.CountAsync());
        Assert.Equal(901, (await db.Animes.SingleAsync(anime => anime.MALId == 500)).AniListId);
    }

    [Fact]
    public async Task Failed_lookup_falls_back_to_review()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { AniListId = 900, Title = "Orbit", SeasonId = season.Id });
        await db.SaveChangesAsync();

        await ImportAsync(db, MalOnly(500, "Orbit"), new AniListIdsHandler(null, HttpStatusCode.BadRequest));

        Assert.Single(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(1, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Candidate_the_import_lists_as_a_relation_is_ruled_out()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        db.Animes.Add(new Anime { MALId = 77, Title = "Orbit", SeasonId = season.Id });
        await db.SaveChangesAsync();
        var incoming = Incoming(10, "Orbit", true);
        incoming.ProviderRelations.Add(new ProviderRelation { RelationType = "PREQUEL", AniListId = 9, MalId = 77 });

        // The lookup fails, so only the relation can rule the candidate out.
        await ImportAsync(db, incoming, new AniListIdsHandler(null, HttpStatusCode.BadRequest));

        Assert.Empty(await db.PendingAnimeReviews.ToListAsync());
        Assert.Equal(2, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Recheck_creates_reviews_whose_candidates_have_different_anilist_ids()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var other = new Anime { AniListId = 13, Title = "Orbit", SeasonId = season.Id };
        db.Animes.Add(other);
        await db.SaveChangesAsync();
        var review = AddReview(season.Id, Incoming(12, "Orbit", true), [other.Id]);
        review.Reason = "Title or alias match needs confirmation";
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();

        Assert.Equal(PendingReviewRecheckOutcome.Created, await CreateCache(db).RecheckPendingReviewAsync(review.Id));

        var saved = await db.PendingAnimeReviews.AsNoTracking().SingleAsync();
        Assert.Equal("Created", saved.Status);
        Assert.Contains("resolved automatically", saved.Reason);
        Assert.Equal(2, await db.Animes.CountAsync());
    }

    [Fact]
    public async Task Recheck_merges_a_mal_only_review_once_anilist_confirms_the_id()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var existing = new Anime { AniListId = 900, Title = "Orbit", SeasonId = season.Id };
        db.Animes.Add(existing);
        await db.SaveChangesAsync();
        var review = new PendingAnimeReview
        {
            SeasonId = season.Id, SourceName = "test", SourceMalId = 500, Reason = "Title or alias match needs confirmation",
            PayloadJson = JsonSerializer.Serialize(new AnimeImportSnapshot { MALId = 500, Title = "Orbit" }),
            CandidateAnimeIdsJson = JsonSerializer.Serialize(new[] { existing.Id })
        };
        db.PendingAnimeReviews.Add(review);
        await db.SaveChangesAsync();

        var outcome = await CreateCache(db, new AniListIdsHandler(new ProviderIds(900, 500))).RecheckPendingReviewAsync(review.Id);

        Assert.Equal(PendingReviewRecheckOutcome.Merged, outcome);
        Assert.Equal(500, (await db.Animes.AsNoTracking().SingleAsync()).MALId);
    }

    [Fact]
    public async Task Backfill_fills_a_missing_mal_id_and_reports_duplicates()
    {
        await using var db = CreateDb();
        var season = await AddSeasonAsync(db);
        var missing = new Anime { AniListId = 900, Title = "Orbit", SeasonId = season.Id };
        var duplicateSource = new Anime { AniListId = 901, Title = "Lantern", SeasonId = season.Id };
        var holder = new Anime { MALId = 600, Title = "Lantern (MAL)", SeasonId = season.Id };
        db.Animes.AddRange(missing, duplicateSource, holder);
        await db.SaveChangesAsync();

        var filled = await CreateCache(db, new AniListIdsHandler(new ProviderIds(900, 500))).BackfillProviderIdsAsync(missing.Id);
        var duplicate = await CreateCache(db, new AniListIdsHandler(new ProviderIds(901, 600))).BackfillProviderIdsAsync(duplicateSource.Id);

        Assert.Equal(ProviderIdBackfillOutcome.Filled, filled.Outcome);
        Assert.Equal(500, (await db.Animes.AsNoTracking().SingleAsync(anime => anime.Id == missing.Id)).MALId);
        Assert.Equal(new ProviderIdBackfillResult(ProviderIdBackfillOutcome.Duplicate, holder.Id), duplicate);
        Assert.Null((await db.Animes.AsNoTracking().SingleAsync(anime => anime.Id == duplicateSource.Id)).MALId);
    }

    private static Task ImportAsync(AnimeOverseer.Server.Data.AnimeDbContext db, Anime incoming, HttpMessageHandler? handler = null)
        => CreateCache(db, handler).FetchAndCacheSeasonsFromAsync(2026, ["spring"], (_, _, _) => Task.FromResult(new List<Anime> { incoming }));

    private static Anime MalOnly(int malId, string title) => new() { MALId = malId, Title = title };

    /// <summary>Answers every AniList request with one Media id/idMal pair, or an error status.</summary>
    private sealed class AniListIdsHandler(ProviderIds? ids, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "graphql.anilist.co") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            Requests.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var media = ids is null ? "null" : $"{{\"id\":{ids.AniListId},\"idMal\":{ids.MalId?.ToString() ?? "null"}}}";
            return new HttpResponseMessage(status) { Content = new StringContent($"{{\"data\":{{\"Media\":{media}}}}}", Encoding.UTF8, "application/json") };
        }
    }
}
