using System.Collections.Concurrent;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Options;

namespace Mercurius.Modules.Teams.Tests;

public class TeamInviteMaintenanceTests
{
    [Fact]
    public async Task RunBatchAsync_BoundsDeterministicMaintenanceAndDoesNotRepublishExpiredInvites()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTime.UtcNow;
        var captain = CreateUser("captain");
        var recipients = Enumerable.Range(0, 8)
            .Select(index => CreateUser($"recipient-{index}"))
            .ToList();
        var team = new Team("Maintenance team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        var dueInvites = Enumerable.Range(0, 4)
            .Select(index => new TeamInvite
            {
                Id = Guid.NewGuid(),
                Team = team,
                TeamId = team.Id,
                UserId = recipients[index].Id,
                Status = TeamInviteStatus.Pending,
                CreatedAt = now.AddDays(-20),
                ExpiresAt = now.AddHours(index - 4)
            })
            .ToList();
        var terminalInvites = new[]
        {
            CreateTerminalInvite(team, recipients[4], TeamInviteStatus.Declined, now.AddDays(-140)),
            CreateTerminalInvite(team, recipients[5], TeamInviteStatus.Cancelled, now.AddDays(-130)),
            CreateTerminalInvite(team, recipients[6], TeamInviteStatus.Expired, now.AddDays(-120)),
            CreateTerminalInvite(team, recipients[7], TeamInviteStatus.Accepted, now.AddDays(-110))
        };

        dbContext.Users.Add(captain);
        dbContext.Users.AddRange(recipients);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().AddRange(dueInvites);
        dbContext.Set<TeamInvite>().AddRange(terminalInvites);
        await dbContext.SaveChangesAsync();

        var publisher = new RecordingTeamEventPublisher();
        var maintenanceService = CreateMaintenanceService(dbContext, publisher, batchSize: 2, eventConcurrency: 2);

        Assert.Equal(4, await maintenanceService.RunBatchAsync());

        dbContext.ChangeTracker.Clear();
        var firstRunInvites = await dbContext.Set<TeamInvite>()
            .AsNoTracking()
            .ToDictionaryAsync(invite => invite.Id);

        Assert.Equal(TeamInviteStatus.Expired, firstRunInvites[dueInvites[0].Id].Status);
        Assert.Equal(TeamInviteStatus.Expired, firstRunInvites[dueInvites[1].Id].Status);
        Assert.Equal(TeamInviteStatus.Pending, firstRunInvites[dueInvites[2].Id].Status);
        Assert.Equal(TeamInviteStatus.Pending, firstRunInvites[dueInvites[3].Id].Status);
        Assert.DoesNotContain(terminalInvites[0].Id, firstRunInvites.Keys);
        Assert.DoesNotContain(terminalInvites[1].Id, firstRunInvites.Keys);
        Assert.Contains(terminalInvites[2].Id, firstRunInvites.Keys);
        Assert.Contains(terminalInvites[3].Id, firstRunInvites.Keys);
        Assert.Equal(
            dueInvites.Take(2).Select(invite => invite.Id).OrderBy(id => id),
            publisher.InviteEvents.Select(inviteEvent => inviteEvent.InviteId).OrderBy(id => id));
        Assert.InRange(publisher.MaxObservedConcurrency, 1, 2);

        Assert.Equal(4, await maintenanceService.RunBatchAsync());
        Assert.Equal(0, await maintenanceService.RunBatchAsync());

        dbContext.ChangeTracker.Clear();
        Assert.All(
            await dbContext.Set<TeamInvite>()
                .Where(invite => dueInvites.Select(dueInvite => dueInvite.Id).Contains(invite.Id))
                .ToListAsync(),
            invite => Assert.Equal(TeamInviteStatus.Expired, invite.Status));
        Assert.False(await dbContext.Set<TeamInvite>()
            .AnyAsync(invite => terminalInvites.Select(terminalInvite => terminalInvite.Id).Contains(invite.Id)));
        Assert.Equal(4, publisher.InviteEvents.Count);
        Assert.All(
            dueInvites,
            invite => Assert.Single(publisher.InviteEvents, inviteEvent => inviteEvent.InviteId == invite.Id));
        Assert.All(
            publisher.InviteEvents,
            inviteEvent => Assert.Equal(nameof(TeamInviteStatus.Expired), inviteEvent.Status));
    }

    [Fact]
    public async Task RunBatchAsync_PreCancelled_DoesNotMutateOrPublish()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var recipient = CreateUser("recipient");
        var team = new Team("Cancellation team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        var invite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = recipient.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddDays(-20),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        };
        dbContext.Users.AddRange(captain, recipient);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(invite);
        await dbContext.SaveChangesAsync();
        var publisher = new RecordingTeamEventPublisher();
        var maintenanceService = CreateMaintenanceService(dbContext, publisher);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            maintenanceService.RunBatchAsync(cancellationSource.Token));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            TeamInviteStatus.Pending,
            (await dbContext.Set<TeamInvite>().SingleAsync()).Status);
        Assert.Empty(publisher.InviteEvents);
    }

    [Fact]
    public void BoundTeamInviteMaintenanceMigration_AddsQuerySpecificPartialIndexes()
    {
        var migration = new BoundTeamInviteMaintenance();
        var indexes = migration.UpOperations
            .OfType<CreateIndexOperation>()
            .ToDictionary(index => index.Name);
        var backfillSql = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;

        Assert.Equal(4, indexes.Count);
        Assert.Contains("SET \"RespondedAt\" = \"CreatedAt\"", backfillSql, StringComparison.Ordinal);
        Assert.Contains("SET \"CancelledAt\" = \"CreatedAt\"", backfillSql, StringComparison.Ordinal);
        Assert.Contains("SET \"ExpiredAt\" = \"CreatedAt\"", backfillSql, StringComparison.Ordinal);
        AssertIndex(indexes, "IX_team_invites_pending_expiration", ["ExpiresAt", "Id"], "\"Status\" = 0");
        AssertIndex(indexes, "IX_team_invites_responded_retention", ["RespondedAt", "Id"], "\"Status\" = 1 OR \"Status\" = 2");
        AssertIndex(indexes, "IX_team_invites_cancelled_retention", ["CancelledAt", "Id"], "\"Status\" = 3");
        AssertIndex(indexes, "IX_team_invites_expired_retention", ["ExpiredAt", "Id"], "\"Status\" = 4");
    }

    [Fact]
    public void TeamInviteModel_DefinesMaintenanceIndexes()
    {
        using var dbContext = CreateDbContext();
        var indexNames = dbContext.Model.FindEntityType(typeof(TeamInvite))!
            .GetIndexes()
            .Select(index => index.GetDatabaseName())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("IX_team_invites_pending_expiration", indexNames);
        Assert.Contains("IX_team_invites_responded_retention", indexNames);
        Assert.Contains("IX_team_invites_cancelled_retention", indexNames);
        Assert.Contains("IX_team_invites_expired_retention", indexNames);
    }

    private static void AssertIndex(
        IReadOnlyDictionary<string, CreateIndexOperation> indexes,
        string name,
        string[] columns,
        string filter)
    {
        var index = indexes[name];
        Assert.Equal("teams", index.Schema);
        Assert.Equal("team_invites", index.Table);
        Assert.Equal(columns, index.Columns);
        Assert.Equal(filter, index.Filter);
    }

    private static TeamInvite CreateTerminalInvite(
        Team team,
        User recipient,
        TeamInviteStatus status,
        DateTime terminalAt)
    {
        return new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = recipient.Id,
            Status = status,
            CreatedAt = terminalAt.AddDays(-14),
            ExpiresAt = terminalAt,
            RespondedAt = status is TeamInviteStatus.Accepted or TeamInviteStatus.Declined ? terminalAt : null,
            CancelledAt = status == TeamInviteStatus.Cancelled ? terminalAt : null,
            ExpiredAt = status == TeamInviteStatus.Expired ? terminalAt : null
        };
    }

    private static TeamInviteMaintenanceService CreateMaintenanceService(
        MercuriusDBContext dbContext,
        ITeamEventPublisher publisher,
        int batchSize = 10,
        int eventConcurrency = 2)
    {
        return new TeamInviteMaintenanceService(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            publisher,
            Options.Create(new TeamInviteMaintenanceOptions
            {
                RetentionDays = 90,
                MaintenanceBatchSize = batchSize,
                MaintenanceIntervalSeconds = 60,
                MaintenanceEventConcurrency = eventConcurrency
            }));
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static User CreateUser(string username)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{Guid.NewGuid():N}",
            Username = username
        };
    }

    private sealed class RecordingTeamEventPublisher : ITeamEventPublisher
    {
        private readonly ConcurrentQueue<RecordedInviteEvent> _inviteEvents = new();
        private int _activePublishers;
        private int _maxObservedConcurrency;

        public IReadOnlyCollection<RecordedInviteEvent> InviteEvents => _inviteEvents.ToArray();
        public int MaxObservedConcurrency => _maxObservedConcurrency;

        public async Task InviteChangedAsync(
            Guid teamId,
            Guid inviteId,
            Guid affectedUserId,
            string status,
            CancellationToken cancellationToken = default)
        {
            var activePublishers = Interlocked.Increment(ref _activePublishers);
            UpdateMaximumConcurrency(activePublishers);

            try
            {
                await Task.Delay(10, cancellationToken);
                _inviteEvents.Enqueue(new RecordedInviteEvent(teamId, inviteId, affectedUserId, status));
            }
            finally
            {
                Interlocked.Decrement(ref _activePublishers);
            }
        }

        public Task MembershipChangedAsync(
            Guid teamId,
            Guid affectedUserId,
            string action,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CaptainTransferredAsync(
            Guid teamId,
            Guid newCaptainUserId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        private void UpdateMaximumConcurrency(int candidate)
        {
            var current = Volatile.Read(ref _maxObservedConcurrency);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxObservedConcurrency, candidate, current);
                if (observed == current)
                    return;

                current = observed;
            }
        }
    }

    private sealed record RecordedInviteEvent(Guid TeamId, Guid InviteId, Guid UserId, string Status);
}
