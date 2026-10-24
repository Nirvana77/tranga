using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations.Manga
{
    /// <inheritdoc />
    public partial class AddMangaPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Mangas",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Mangas");
        }
    }
}
