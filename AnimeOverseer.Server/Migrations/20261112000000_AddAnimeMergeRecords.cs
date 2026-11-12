using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

/// <summary>History of duplicate catalogue rows merged into one from the Possible duplicates tab.</summary>
[DbContext(typeof(AnimeDbContext))]
[Migration("20261112000000_AddAnimeMergeRecords")]
public partial class AddAnimeMergeRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AnimeMergeRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                KeptAnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                RemovedAnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                RemovedTitle = table.Column<string>(type: "TEXT", nullable: false),
                RemovedAniListId = table.Column<int>(type: "INTEGER", nullable: true),
                RemovedMalId = table.Column<int>(type: "INTEGER", nullable: true),
                RemovedKitsuId = table.Column<int>(type: "INTEGER", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: true),
                MergedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AnimeMergeRecords", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_AnimeMergeRecords_KeptAnimeId",
            table: "AnimeMergeRecords",
            column: "KeptAnimeId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AnimeMergeRecords");
    }
}
