using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class RestrictCrossModuleDeletes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_placement_teams_teams_TeamId",
                schema: "tournament",
                table: "placement_teams");

            migrationBuilder.DropForeignKey(
                name: "FK_placement_users_users_UserId",
                schema: "tournament",
                table: "placement_users");

            migrationBuilder.DropForeignKey(
                name: "FK_team_invites_users_UserId",
                schema: "teams",
                table: "team_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_team_members_users_UserId",
                schema: "teams",
                table: "team_members");

            migrationBuilder.DropForeignKey(
                name: "FK_tournament_sponsor_placements_tournaments_TournamentId",
                schema: "sponsorship",
                table: "tournament_sponsor_placements");

            migrationBuilder.AddForeignKey(
                name: "FK_placement_teams_teams_TeamId",
                schema: "tournament",
                table: "placement_teams",
                column: "TeamId",
                principalSchema: "teams",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_placement_users_users_UserId",
                schema: "tournament",
                table: "placement_users",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_team_invites_users_UserId",
                schema: "teams",
                table: "team_invites",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_team_members_users_UserId",
                schema: "teams",
                table: "team_members",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_tournament_sponsor_placements_tournaments_TournamentId",
                schema: "sponsorship",
                table: "tournament_sponsor_placements",
                column: "TournamentId",
                principalSchema: "tournament",
                principalTable: "tournaments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_placement_teams_teams_TeamId",
                schema: "tournament",
                table: "placement_teams");

            migrationBuilder.DropForeignKey(
                name: "FK_placement_users_users_UserId",
                schema: "tournament",
                table: "placement_users");

            migrationBuilder.DropForeignKey(
                name: "FK_team_invites_users_UserId",
                schema: "teams",
                table: "team_invites");

            migrationBuilder.DropForeignKey(
                name: "FK_team_members_users_UserId",
                schema: "teams",
                table: "team_members");

            migrationBuilder.DropForeignKey(
                name: "FK_tournament_sponsor_placements_tournaments_TournamentId",
                schema: "sponsorship",
                table: "tournament_sponsor_placements");

            migrationBuilder.AddForeignKey(
                name: "FK_placement_teams_teams_TeamId",
                schema: "tournament",
                table: "placement_teams",
                column: "TeamId",
                principalSchema: "teams",
                principalTable: "teams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_placement_users_users_UserId",
                schema: "tournament",
                table: "placement_users",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_team_invites_users_UserId",
                schema: "teams",
                table: "team_invites",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_team_members_users_UserId",
                schema: "teams",
                table: "team_members",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_tournament_sponsor_placements_tournaments_TournamentId",
                schema: "sponsorship",
                table: "tournament_sponsor_placements",
                column: "TournamentId",
                principalSchema: "tournament",
                principalTable: "tournaments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
