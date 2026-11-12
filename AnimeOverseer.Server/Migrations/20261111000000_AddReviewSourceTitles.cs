using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

/// <summary>
/// Copies each review's incoming title out of its JSON payload, and each remembered
/// decision's source title, into columns so the review lists can search, sort and page in SQL.
/// </summary>
[DbContext(typeof(AnimeDbContext))]
[Migration("20261111000000_AddReviewSourceTitles")]
public partial class AddReviewSourceTitles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "SourceTitle", table: "PendingAnimeReviews", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "SourceTitle", table: "AnimeMatchDecisions", type: "TEXT", nullable: true);

        migrationBuilder.Sql("""
            UPDATE "PendingAnimeReviews"
            SET "SourceTitle" = COALESCE(
                NULLIF(TRIM(json_extract("PayloadJson", '$.Title')), ''),
                NULLIF(TRIM(json_extract("PayloadJson", '$.OriginalTitle')), ''))
            WHERE json_valid("PayloadJson");
            """);

        // A decision's title comes from the newest review of the same provider record,
        // matched the same way the Remembered tab used to look it up.
        migrationBuilder.Sql("""
            UPDATE "AnimeMatchDecisions"
            SET "SourceTitle" = (
                SELECT r."SourceTitle" FROM "PendingAnimeReviews" AS r
                WHERE r."SourceTitle" IS NOT NULL AND (
                    ("AnimeMatchDecisions"."SourceAniListId" > 0 AND r."SourceAniListId" = "AnimeMatchDecisions"."SourceAniListId") OR
                    ("AnimeMatchDecisions"."SourceMalId" > 0 AND r."SourceMalId" = "AnimeMatchDecisions"."SourceMalId"))
                ORDER BY r."CreatedAt" DESC
                LIMIT 1);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SourceTitle", table: "AnimeMatchDecisions");
        migrationBuilder.DropColumn(name: "SourceTitle", table: "PendingAnimeReviews");
    }
}
