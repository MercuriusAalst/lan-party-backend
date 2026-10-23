using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class Auth0AccountLifecycle : Migration
    {
        public const string LegacyCleanupRepairSql = """
            DO $$
            DECLARE affected integer;
            BEGIN
                LOOP
                    WITH legacy AS MATERIALIZED (
                        SELECT
                            u."Id" AS user_id,
                            u."Auth0UserId" AS subject,
                            COALESCE(u."DeletedAtUtc", u."UpdatedAtUtc") AS requested_at,
                            gen_random_uuid() AS cleanup_id,
                            gen_random_uuid() AS message_id
                        FROM identity.users AS u
                        WHERE u."IsDeleted"
                          AND u."Auth0UserId" NOT LIKE 'tombstone:%'
                        ORDER BY u."Id"
                        LIMIT 100
                        FOR UPDATE OF u
                    ), jobs AS (
                        INSERT INTO identity.auth0_account_cleanups
                            ("Id", "UserId", "SubjectHash", "Subject", "RequestedAtUtc", "OutboxMessageId")
                        SELECT
                            cleanup_id,
                            user_id,
                            encode(sha256(convert_to(subject, 'UTF8')), 'hex'),
                            subject,
                            requested_at,
                            message_id
                        FROM legacy
                        RETURNING "Id", "UserId", "OutboxMessageId"
                    ), messages AS (
                        INSERT INTO platform.outbox_messages
                            (id, event_type, payload, occurred_at_utc, retry_count, next_attempt_at_utc)
                        SELECT
                            j."OutboxMessageId",
                            'Mercurius.Modules.Identity.Contracts.Auth0AccountCleanupRequestedIntegrationEvent',
                            json_build_object('cleanupId', j."Id", 'userId', j."UserId")::text,
                            now(),
                            0,
                            now()
                        FROM jobs AS j
                        RETURNING id
                    )
                    UPDATE identity.users AS u
                    SET "Auth0UserId" = 'tombstone:' || replace(u."Id"::text, '-', '')
                    FROM jobs AS j
                    JOIN messages AS m ON m.id = j."OutboxMessageId"
                    WHERE u."Id" = j."UserId";

                    GET DIAGNOSTICS affected = ROW_COUNT;
                    EXIT WHEN affected = 0;
                END LOOP;
            END
            $$;
            """;

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

            migrationBuilder.Sql(LegacyCleanupRepairSql);
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
