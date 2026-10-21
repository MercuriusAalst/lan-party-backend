using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mercurius.LAN.API.Migrations
{
    /// <inheritdoc />
    public partial class TeamMemberJoinedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "JoinedAtUtc",
                schema: "teams",
                table: "team_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(EnqueueLegacyDeletedUserCleanupSql);
        }

        // Accounts deleted before Teams and Tournament consumed the user-deleted event may still be members,
        // captains, pending invitees or Scheduled/InProgress participants. Replay the event through the outbox (the
        // same row shape ModuleEventPublisher writes) so the idempotent, inbox-deduplicated handlers repair them.
        // Users that already have an undelivered replay are skipped, so running the step again adds nothing. The
        // first attempt is delayed so an instance from the previous release cannot acknowledge the rows without
        // the new handlers during a rolling deployment.
        public const string EnqueueLegacyDeletedUserCleanupSql = """
            INSERT INTO platform.outbox_messages (id, event_type, payload, occurred_at_utc, retry_count, next_attempt_at_utc)
            SELECT
                gen_random_uuid(),
                'Mercurius.Modules.Identity.Contracts.UserDeletedIntegrationEvent',
                json_build_object(
                    'userId', json_build_object('value', u."Id"),
                    'deletedAtUtc', to_char(COALESCE(u."DeletedAtUtc", u."UpdatedAtUtc") AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))::text,
                now(),
                0,
                now() + interval '10 minutes'
            FROM identity.users u
            WHERE u."IsDeleted"
              AND (
                  EXISTS (
                      SELECT 1 FROM teams.team_members m
                      JOIN teams.teams t ON t."Id" = m."TeamId"
                      WHERE m."UserId" = u."Id" AND NOT t."IsDeleted")
                  OR EXISTS (
                      SELECT 1 FROM teams.teams t
                      WHERE t."CaptainUserId" = u."Id" AND NOT t."IsDeleted")
                  OR EXISTS (
                      SELECT 1 FROM teams.team_invites i
                      JOIN teams.teams t ON t."Id" = i."TeamId"
                      WHERE i."UserId" = u."Id" AND i."Status" = 0 AND NOT t."IsDeleted")
                  OR EXISTS (
                      SELECT 1 FROM tournament.roster_members r
                      JOIN tournament.tournaments g ON g."Id" = r."TournamentId"
                      WHERE r."UserId" = u."Id" AND g."Status" IN (0, 1))
                  OR EXISTS (
                      SELECT 1 FROM tournament.tournament_registrations reg
                      JOIN tournament.tournaments g ON g."Id" = reg."TournamentId"
                      WHERE reg."UserId" = u."Id" AND g."Status" IN (0, 1)))
              AND NOT EXISTS (
                  SELECT 1 FROM platform.outbox_messages o
                  WHERE o.event_type = 'Mercurius.Modules.Identity.Contracts.UserDeletedIntegrationEvent'
                    AND o.processed_at_utc IS NULL
                    AND o.dead_lettered_at_utc IS NULL
                    AND (o.payload::jsonb -> 'userId' ->> 'value') = u."Id"::text);
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "JoinedAtUtc",
                schema: "teams",
                table: "team_members");
        }
    }
}
