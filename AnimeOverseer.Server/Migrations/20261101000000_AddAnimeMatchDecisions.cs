using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

[DbContext(typeof(AnimeDbContext))]
[Migration("20261101000000_AddAnimeMatchDecisions")]
public partial class AddAnimeMatchDecisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "DeferredUntil",
            table: "PendingAnimeReviews",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AnimeMatchDecisions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SourceAniListId = table.Column<int>(type: "INTEGER", nullable: true),
                SourceMalId = table.Column<int>(type: "INTEGER", nullable: true),
                AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                IsSameAnime = table.Column<bool>(type: "INTEGER", nullable: false),
                Origin = table.Column<string>(type: "TEXT", nullable: false),
                DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnimeMatchDecisions", x => x.Id);
                table.ForeignKey(
                    name: "FK_AnimeMatchDecisions_Animes_AnimeId",
                    column: x => x.AnimeId,
                    principalTable: "Animes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AnimeMatchDecisions_AnimeId",
            table: "AnimeMatchDecisions",
            column: "AnimeId");

        migrationBuilder.CreateIndex(
            name: "IX_AnimeMatchDecisions_SourceAniListId",
            table: "AnimeMatchDecisions",
            column: "SourceAniListId");

        migrationBuilder.CreateIndex(
            name: "IX_AnimeMatchDecisions_SourceMalId",
            table: "AnimeMatchDecisions",
            column: "SourceMalId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AnimeMatchDecisions");
        migrationBuilder.DropColumn(name: "DeferredUntil", table: "PendingAnimeReviews");
    }
}
