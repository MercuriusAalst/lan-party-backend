using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class ThemeAwareSponsorLogos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LightLogoUrl",
                schema: "sponsorship",
                table: "sponsors",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LightLogoUrl",
                schema: "sponsorship",
                table: "sponsors");
        }
    }
}
