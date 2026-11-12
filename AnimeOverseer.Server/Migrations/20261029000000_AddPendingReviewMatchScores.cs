using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

[DbContext(typeof(AnimeDbContext))]
[Migration("20261029000000_AddPendingReviewMatchScores")]
public partial class AddPendingReviewMatchScores : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing reviews stay null; AnimeReviewService scores them on read.
        migrationBuilder.AddColumn<int>(
            name: "MatchScore",
            table: "PendingAnimeReviews",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MatchEvidenceJson",
            table: "PendingAnimeReviews",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MatchScore", table: "PendingAnimeReviews");
        migrationBuilder.DropColumn(name: "MatchEvidenceJson", table: "PendingAnimeReviews");
    }
}
