using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class Auth0AccountLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth0_account_cleanups",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProviderCreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SanitizedOutcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth0_account_cleanups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auth0_account_cleanups_OutboxMessageId",
                schema: "identity",
                table: "auth0_account_cleanups",
                column: "OutboxMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth0_account_cleanups_SubjectHash",
                schema: "identity",
                table: "auth0_account_cleanups",
                column: "SubjectHash");

            migrationBuilder.CreateIndex(
                name: "IX_auth0_account_cleanups_UserId_RequestedAtUtc",
                schema: "identity",
                table: "auth0_account_cleanups",
                columns: new[] { "UserId", "RequestedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth0_account_cleanups",
                schema: "identity");

        }
    }
}
