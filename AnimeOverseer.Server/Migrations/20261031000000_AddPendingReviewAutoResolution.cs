using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

[DbContext(typeof(AnimeDbContext))]
[Migration("20261031000000_AddPendingReviewAutoResolution")]
public partial class AddPendingReviewAutoResolution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ResolvedAnimeId",
            table: "PendingAnimeReviews",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "AutoResolved",
            table: "PendingAnimeReviews",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "PreMergeSnapshotJson",
            table: "PendingAnimeReviews",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "AutoDecisionUndone",
            table: "PendingAnimeReviews",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ResolvedAnimeId", table: "PendingAnimeReviews");
        migrationBuilder.DropColumn(name: "AutoResolved", table: "PendingAnimeReviews");
        migrationBuilder.DropColumn(name: "PreMergeSnapshotJson", table: "PendingAnimeReviews");
        migrationBuilder.DropColumn(name: "AutoDecisionUndone", table: "PendingAnimeReviews");
    }
}
