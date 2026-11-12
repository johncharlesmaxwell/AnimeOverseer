using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

[DbContext(typeof(AnimeDbContext))]
[Migration("20261110000000_AddAnimeSourceTitles")]
public partial class AddAnimeSourceTitles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NameFromSources",
            table: "Animes",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "AnimeSourceTitles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                AnimeId = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AnimeSourceTitles", x => x.Id);
                table.ForeignKey(
                    name: "FK_AnimeSourceTitles_Animes_AnimeId",
                    column: x => x.AnimeId,
                    principalTable: "Animes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AnimeSourceTitles_AnimeId_Source",
            table: "AnimeSourceTitles",
            columns: new[] { "AnimeId", "Source" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AnimeSourceTitles");
        migrationBuilder.DropColumn(name: "NameFromSources", table: "Animes");
    }
}
