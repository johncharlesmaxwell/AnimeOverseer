using AnimeOverseer.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeOverseer.Server.Migrations;

[DbContext(typeof(AnimeDbContext))]
[Migration("20261007000000_AddCollectionHomepageQuickFilter")]
public partial class AddCollectionHomepageQuickFilter : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "ShowOnHomepage",
            table: "AnimeCollections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ShowOnHomepage", table: "AnimeCollections");
    }
}
