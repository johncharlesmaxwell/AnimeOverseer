using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

/// <summary>
/// Indexes for lookups that previously scanned whole tables: a job's logs (read on every
/// job-page poll) and pending reviews by source provider ID (checked for every queued
/// catalogue review).
/// </summary>
[DbContext(typeof(AnimeDbContext))]
[Migration("20261113000000_AddLookupIndexes")]
public partial class AddLookupIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_SyncJobLogs_SyncJobId_Id",
            table: "SyncJobLogs",
            columns: ["SyncJobId", "Id"]);

        migrationBuilder.CreateIndex(
            name: "IX_PendingAnimeReviews_SourceAniListId",
            table: "PendingAnimeReviews",
            column: "SourceAniListId");

        migrationBuilder.CreateIndex(
            name: "IX_PendingAnimeReviews_SourceMalId",
            table: "PendingAnimeReviews",
            column: "SourceMalId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_SyncJobLogs_SyncJobId_Id", table: "SyncJobLogs");
        migrationBuilder.DropIndex(name: "IX_PendingAnimeReviews_SourceAniListId", table: "PendingAnimeReviews");
        migrationBuilder.DropIndex(name: "IX_PendingAnimeReviews_SourceMalId", table: "PendingAnimeReviews");
    }
}
