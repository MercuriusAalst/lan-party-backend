using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class FeaturedHomepageTournamentCountRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_featured_homepage_tournaments_count",
                schema: "tournament",
                table: "featured_homepage_tournaments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_featured_homepage_tournaments_count",
                schema: "tournament",
                table: "featured_homepage_tournaments",
                sql: "cardinality(\"TournamentIds\") BETWEEN 1 AND 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_featured_homepage_tournaments_count",
                schema: "tournament",
                table: "featured_homepage_tournaments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_featured_homepage_tournaments_count",
                schema: "tournament",
                table: "featured_homepage_tournaments",
                sql: "cardinality(\"TournamentIds\") = 4");
        }
    }
}
