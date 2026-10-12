using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class TournamentPrizesAndContactAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstPlacePrize",
                schema: "tournament",
                table: "tournaments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondPlacePrize",
                schema: "tournament",
                table: "tournaments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThirdPlacePrize",
                schema: "tournament",
                table: "tournaments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstPlacePrize",
                schema: "tournament",
                table: "tournaments");

            migrationBuilder.DropColumn(
                name: "SecondPlacePrize",
                schema: "tournament",
                table: "tournaments");

            migrationBuilder.DropColumn(
                name: "ThirdPlacePrize",
                schema: "tournament",
                table: "tournaments");
        }
    }
}
