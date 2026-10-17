using Mercurius.LAN.API.Data;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.Extensions.Options;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using ContractResolutionReason = Mercurius.Modules.Tournament.Contracts.MatchResolutionReason;

namespace Mercurius.Modules.Tournament.Tests;

public class PendingMatchResolutionTests
{
    private static readonly DateTime NowUtc = new(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetPendingResolutionsAsync_ReturnsOnlyMatchesOfTheCallersAssignedTournaments()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Assigned Cup", assignedAdmin.Id);
        var missingReport = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var disputed = CreatePendingMatch(tournament, player1, player2, bothReports: true);
        tournament.Matches.Add(missingReport);
        tournament.Matches.Add(disputed);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        var assigned = await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId);
        var other = await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);

        Assert.Empty(other);
        Assert.Equal(2, assigned.Count);
        var missing = assigned.Single(item => item.MatchId == missingReport.Id);
        Assert.Equal(ContractResolutionReason.MissingOpponentReport, missing.Reason);
        Assert.Equal(tournament.Id, missing.TournamentId);
        Assert.Equal("Assigned Cup", missing.TournamentName);
        Assert.Equal("player-one", missing.Participant1Name);
        Assert.Equal("player-two", missing.Participant2Name);
        Assert.Equal(1, missing.Participant1ReportedScore1);
        Assert.Equal(0, missing.Participant1ReportedScore2);
        Assert.Null(missing.Participant2ReportedScore1);
        Assert.Null(missing.Participant2ReportedScore2);
        var conflict = assigned.Single(item => item.MatchId == disputed.Id);
        Assert.Equal(ContractResolutionReason.Disputed, conflict.Reason);
        Assert.Equal(0, conflict.Participant2ReportedScore1);
        Assert.Equal(1, conflict.Participant2ReportedScore2);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_LegacyTournamentWithoutAdmin_IsVisibleToEveryAdmin()
    {
        var firstAdmin = CreateUser("first-admin");
        var secondAdmin = CreateUser("second-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Legacy Cup", assignedAdminUserId: null);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [firstAdmin, secondAdmin, player1, player2]);
        var service = CreateService(dbContext, [firstAdmin, secondAdmin, player1, player2]);

        var first = await service.GetPendingResolutionsAsync(firstAdmin.Auth0UserId);
        var second = await service.GetPendingResolutionsAsync(secondAdmin.Auth0UserId);

        Assert.Equal(match.Id, Assert.Single(first).MatchId);
        Assert.Equal(match.Id, Assert.Single(second).MatchId);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ExcludesMatchesThatDoNotNeedResolutionOrCannotBeResolved()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Active Cup", admin.Id);
        var waiting = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.LifecycleState, MatchLifecycleState.ScoreConfirmation);
        var disputedInWindow = CreatePendingMatch(tournament, player1, player2, bothReports: true)
            .Set(x => x.LifecycleState, MatchLifecycleState.Disputed);
        var completed = CreatePendingMatch(tournament, player1, player2, bothReports: true)
            .Set(x => x.LifecycleState, MatchLifecycleState.Completed);
        tournament.Matches.Add(waiting);
        tournament.Matches.Add(disputedInWindow);
        tournament.Matches.Add(completed);
        var completedTournament = CreateTournament("Finished Cup", admin.Id)
            .Set(x => x.Status, TournamentStatus.Completed);
        completedTournament.Matches.Add(CreatePendingMatch(completedTournament, player1, player2, bothReports: false));
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament, completedTournament], [admin, player1, player2]);
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = await service.GetPendingResolutionsAsync(admin.Auth0UserId);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ReportsRequiredSinceFromTheLatestNotification()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Notified Cup", admin.Id);
        var notified = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var unnotified = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(notified);
        tournament.Matches.Add(unnotified);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().AddRange(
            CreateNotification(notified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(notified, admin.Id, NowUtc.AddMinutes(-10)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = await service.GetPendingResolutionsAsync(admin.Auth0UserId);

        Assert.Equal(2, pending.Count);
        Assert.Equal(notified.Id, pending[0].MatchId);
        Assert.Equal(NowUtc.AddMinutes(-10), pending[0].RequiredSinceUtc);
        Assert.Equal(unnotified.Id, pending[1].MatchId);
        Assert.Null(pending[1].RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_IgnoresNotificationsFromBeforeTheLastReversal()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Reversed Cup", admin.Id);
        // Resolved, reversed at -5 minutes, then escalated again: the -30 minute notification is from the first cycle.
        var reEscalatedNotYetNotified = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-5));
        var reEscalatedAndNotified = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-5));
        tournament.Matches.Add(reEscalatedNotYetNotified);
        tournament.Matches.Add(reEscalatedAndNotified);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().AddRange(
            CreateNotification(reEscalatedNotYetNotified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(reEscalatedAndNotified, admin.Id, NowUtc.AddMinutes(-30)),
            CreateNotification(reEscalatedAndNotified, admin.Id, NowUtc.AddMinutes(-1)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [admin, player1, player2]);

        var pending = (await service.GetPendingResolutionsAsync(admin.Auth0UserId)).ToDictionary(item => item.MatchId);

        Assert.Null(pending[reEscalatedNotYetNotified.Id].RequiredSinceUtc);
        Assert.Equal(NowUtc.AddMinutes(-1), pending[reEscalatedAndNotified.Id].RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_UsesTeamNamesForTeamMatches()
    {
        var admin = CreateUser("assigned-admin");
        var captain1 = CreateUser("captain-one");
        var captain2 = CreateUser("captain-two");
        var team1 = new Team("Alpha", captain1.Id) { Id = Guid.NewGuid() };
        var team2 = new Team("Beta", captain2.Id) { Id = Guid.NewGuid() };
        var tournament = CreateTournament("Team Cup", admin.Id)
            .Set(x => x.ParticipationMode, ParticipationMode.Team);
        tournament.Matches.Add(new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Team)
            .Set(x => x.LifecycleState, MatchLifecycleState.AdminResolutionRequired)
            .Set(x => x.TeamParticipant1Id, team1.Id)
            .Set(x => x.TeamParticipant2Id, team2.Id)
            .Set(x => x.Participant2ReportedScore1, 0)
            .Set(x => x.Participant2ReportedScore2, 1));
        await using var dbContext = CreateDbContext();
        dbContext.Teams.AddRange(team1, team2);
        await SaveAsync(dbContext, [tournament], [admin, captain1, captain2]);
        var service = CreateService(dbContext, [admin, captain1, captain2], [team1, team2]);

        var item = Assert.Single(await service.GetPendingResolutionsAsync(admin.Auth0UserId));

        Assert.Equal("Alpha", item.Participant1Name);
        Assert.Equal("Beta", item.Participant2Name);
        Assert.Equal(ContractResolutionReason.MissingOpponentReport, item.Reason);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ClearsItselfOnceTheMatchIsResolved()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Resolved Cup", admin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [admin, player1, player2]);
        var service = CreateService(dbContext, [admin, player1, player2]);
        Assert.Single(await service.GetPendingResolutionsAsync(admin.Auth0UserId));

        await service.ResolveAsync(
            match.Id,
            admin.Auth0UserId,
            new ResolveMatchDTO { Participant1Score = 0, Participant2Score = 1 });

        Assert.Empty(await service.GetPendingResolutionsAsync(admin.Auth0UserId));
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_LabelsAssignedAndUnassigned()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var assigned = CreateTournament("Assigned Cup", assignedAdmin.Id);
        var legacy = CreateTournament("Legacy Cup", null);
        var assignedMatch = CreatePendingMatch(assigned, player1, player2, bothReports: false);
        var legacyMatch = CreatePendingMatch(legacy, player1, player2, bothReports: false);
        assigned.Matches.Add(assignedMatch);
        legacy.Matches.Add(legacyMatch);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [assigned, legacy], [assignedAdmin, otherAdmin, player1, player2]);
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        var forAssigned = (await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId)).ToDictionary(item => item.MatchId);
        var forOther = (await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId)).ToDictionary(item => item.MatchId);

        Assert.Equal(PendingMatchAssignment.Assigned, forAssigned[assignedMatch.Id].Assignment);
        Assert.Equal(PendingMatchAssignment.Unassigned, forAssigned[legacyMatch.Id].Assignment);
        Assert.Equal(PendingMatchAssignment.Unassigned, forOther[legacyMatch.Id].Assignment);
        Assert.DoesNotContain(assignedMatch.Id, forOther.Keys);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_AssignedAdminNoLongerAdministrator_IsVisibleToEveryAdmin()
    {
        var formerAdmin = CreateUser("former-assigned");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Orphaned Cup", formerAdmin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [formerAdmin, otherAdmin, player1, player2]);
        // The assigned user lost the admin role: the identity module no longer reports them as an administrator.
        var service = CreateService(dbContext, [formerAdmin, otherAdmin, player1, player2], adminUserIds: [otherAdmin.Id]);

        var item = Assert.Single(await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId));

        Assert.Equal(match.Id, item.MatchId);
        Assert.Equal(PendingMatchAssignment.AssignedAdminUnavailable, item.Assignment);
        // No current-cycle notification yet: still listed because the assigned admin cannot act.
        Assert.Null(item.RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_EscalatesAfterTheConfiguredWaitToEveryAdmin()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Slow Cup", assignedAdmin.Id);
        var stale = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var fresh = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(stale);
        tournament.Matches.Add(fresh);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().AddRange(
            CreateNotification(stale, assignedAdmin.Id, NowUtc.AddMinutes(-10)),
            CreateNotification(fresh, assignedAdmin.Id, NowUtc.AddMinutes(-9)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        var forOther = await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        var forAssigned = (await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId)).ToDictionary(item => item.MatchId);

        var escalated = Assert.Single(forOther);
        Assert.Equal(stale.Id, escalated.MatchId);
        Assert.Equal(PendingMatchAssignment.Escalated, escalated.Assignment);
        Assert.Equal(PendingMatchAssignment.Assigned, forAssigned[stale.Id].Assignment);
        Assert.Equal(PendingMatchAssignment.Assigned, forAssigned[fresh.Id].Assignment);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_UsesTheConfiguredEscalationWait()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Configured Cup", assignedAdmin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().Add(CreateNotification(match, assignedAdmin.Id, NowUtc.AddMinutes(-3)));
        await dbContext.SaveChangesAsync();
        var users = new[] { assignedAdmin, otherAdmin, player1, player2 };

        var defaultWait = await CreateService(dbContext, users).GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        var shortWait = await CreateService(dbContext, users, escalationAfterMinutes: 2).GetPendingResolutionsAsync(otherAdmin.Auth0UserId);

        Assert.Empty(defaultWait);
        Assert.Equal(PendingMatchAssignment.Escalated, Assert.Single(shortWait).Assignment);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_LabelPriority_UnavailableBeatsEscalated_AssignedBeatsBoth()
    {
        var formerAdmin = CreateUser("former-assigned");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Priority Cup", formerAdmin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [formerAdmin, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().Add(CreateNotification(match, formerAdmin.Id, NowUtc.AddMinutes(-60)));
        await dbContext.SaveChangesAsync();
        var users = new[] { formerAdmin, otherAdmin, player1, player2 };

        var asOther = await CreateService(dbContext, users, adminUserIds: [otherAdmin.Id])
            .GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        var asAssigned = await CreateService(dbContext, users, adminUserIds: [otherAdmin.Id])
            .GetPendingResolutionsAsync(formerAdmin.Auth0UserId);

        Assert.Equal(PendingMatchAssignment.AssignedAdminUnavailable, Assert.Single(asOther).Assignment);
        Assert.Equal(PendingMatchAssignment.Assigned, Assert.Single(asAssigned).Assignment);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_UnknownRequiredSince_IsNotEscalatedForAValidAssignedAdmin()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Unknown Wait Cup", assignedAdmin.Id);
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-120));
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        Assert.Empty(await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId));
        Assert.Null(Assert.Single(await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId)).RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_WhenAdminLookupFails_TreatsAssignedAdminAsAvailable()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Outage Cup", assignedAdmin.Id);
        var waiting = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        var stale = CreatePendingMatch(tournament, player1, player2, bothReports: false);
        tournament.Matches.Add(waiting);
        tournament.Matches.Add(stale);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().Add(CreateNotification(stale, assignedAdmin.Id, NowUtc.AddMinutes(-30)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(
            dbContext,
            [assignedAdmin, otherAdmin, player1, player2],
            adminLookupFailure: new ServiceUnavailableException("Auth0 is unavailable."));

        var forOther = await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);

        // The failure never broadens visibility: only the escalated match shows, labelled by the escalation rule.
        var item = Assert.Single(forOther);
        Assert.Equal(stale.Id, item.MatchId);
        Assert.Equal(PendingMatchAssignment.Escalated, item.Assignment);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_ChecksEachOtherAssignedAdminOncePerCacheWindow()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var own = CreateTournament("Own Cup", otherAdmin.Id);
        var legacy = CreateTournament("Legacy Cup", null);
        var theirs = CreateTournament("Their Cup", assignedAdmin.Id);
        own.Matches.Add(CreatePendingMatch(own, player1, player2, bothReports: false));
        legacy.Matches.Add(CreatePendingMatch(legacy, player1, player2, bothReports: false));
        theirs.Matches.Add(CreatePendingMatch(theirs, player1, player2, bothReports: false));
        theirs.Matches.Add(CreatePendingMatch(theirs, player1, player2, bothReports: false));
        theirs.Matches.Add(CreatePendingMatch(theirs, player1, player2, bothReports: false));
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [own, legacy, theirs], [assignedAdmin, otherAdmin, player1, player2]);
        var users = new[] { assignedAdmin, otherAdmin, player1, player2 };
        var clock = new FixedTimeProvider(NowUtc);
        var identity = new CountingIdentityModule(TournamentTestSupport.CreateIdentityModule(
            users,
            adminUserIds: [assignedAdmin.Id, otherAdmin.Id]));
        var service = CreateService(dbContext, users, identityModule: identity, timeProvider: clock);

        // From the assigned admin's view only other-admin's tournament belongs to someone else: one lookup.
        await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId);
        Assert.Equal(1, identity.AdminLookupCount);
        // From other-admin's view the three matches of assigned-admin's tournament cost a single lookup.
        await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        Assert.Equal(2, identity.AdminLookupCount);
        // Repeated polls inside the TTL are answered from the cache.
        await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId);
        Assert.Equal(2, identity.AdminLookupCount);

        clock.Advance(AssignedAdminAvailabilityCache.TimeToLive + TimeSpan.FromSeconds(1));
        await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        Assert.Equal(3, identity.AdminLookupCount);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_HangingAdminLookups_AreCutOffTogetherAndNeverWidenVisibility()
    {
        var firstAssigned = CreateUser("first-assigned-admin");
        var secondAssigned = CreateUser("second-assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var firstTournament = CreateTournament("First Cup", firstAssigned.Id);
        var secondTournament = CreateTournament("Second Cup", secondAssigned.Id);
        var firstMatch = CreatePendingMatch(firstTournament, player1, player2, bothReports: false);
        var escalated = CreatePendingMatch(secondTournament, player1, player2, bothReports: false);
        firstTournament.Matches.Add(firstMatch);
        secondTournament.Matches.Add(escalated);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [firstTournament, secondTournament], [firstAssigned, secondAssigned, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().Add(CreateNotification(escalated, secondAssigned.Id, NowUtc.AddMinutes(-30)));
        await dbContext.SaveChangesAsync();
        var users = new[] { firstAssigned, secondAssigned, otherAdmin, player1, player2 };
        var identity = new CountingIdentityModule(
            TournamentTestSupport.CreateIdentityModule(users),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return false;
            });
        var service = CreateService(
            dbContext,
            users,
            identityModule: identity,
            adminLookupTimeout: TimeSpan.FromMilliseconds(250));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var items = await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        stopwatch.Stop();

        // One shared deadline bounds the batch; the timed-out admins count as still valid, so only the escalated match shows.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Lookups took {stopwatch.Elapsed}.");
        var item = Assert.Single(items);
        Assert.Equal(escalated.Id, item.MatchId);
        Assert.Equal(PendingMatchAssignment.Escalated, item.Assignment);
        // The fail-open answer is cached: the next poll does not hit the lookup again.
        var lookupsAfterFirstPoll = identity.AdminLookupCount;
        await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);
        Assert.Equal(lookupsAfterFirstPoll, identity.AdminLookupCount);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_AdminLookupsRunSequentiallyBecauseTheIdentityModuleSharesADbContext()
    {
        var firstAssigned = CreateUser("first-assigned-admin");
        var secondAssigned = CreateUser("second-assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var firstTournament = CreateTournament("First Cup", firstAssigned.Id);
        var secondTournament = CreateTournament("Second Cup", secondAssigned.Id);
        firstTournament.Matches.Add(CreatePendingMatch(firstTournament, player1, player2, bothReports: false));
        secondTournament.Matches.Add(CreatePendingMatch(secondTournament, player1, player2, bothReports: false));
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [firstTournament, secondTournament], [firstAssigned, secondAssigned, otherAdmin, player1, player2]);
        var users = new[] { firstAssigned, secondAssigned, otherAdmin, player1, player2 };
        var inFlight = 0;
        var maxInFlight = 0;
        var identity = new CountingIdentityModule(
            TournamentTestSupport.CreateIdentityModule(users, adminUserIds: [firstAssigned.Id, secondAssigned.Id]),
            async (_, _) =>
            {
                maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return true;
            });
        var service = CreateService(dbContext, users, identityModule: identity);

        await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId);

        Assert.Equal(2, identity.AdminLookupCount);
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_OldCycleNotificationOlderThanTheWait_DoesNotEscalateAfterAReversal()
    {
        var assignedAdmin = CreateUser("assigned-admin");
        var otherAdmin = CreateUser("other-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var tournament = CreateTournament("Reversed Slow Cup", assignedAdmin.Id);
        // Reversed five minutes ago and escalated again; the only notification is from the first cycle, 30 minutes ago.
        var match = CreatePendingMatch(tournament, player1, player2, bothReports: false)
            .Set(x => x.ResultRecordedAtUtc, NowUtc.AddMinutes(-5));
        tournament.Matches.Add(match);
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [tournament], [assignedAdmin, otherAdmin, player1, player2]);
        dbContext.Set<MatchResolutionNotification>().Add(CreateNotification(match, assignedAdmin.Id, NowUtc.AddMinutes(-30)));
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, [assignedAdmin, otherAdmin, player1, player2]);

        Assert.Empty(await service.GetPendingResolutionsAsync(otherAdmin.Auth0UserId));
        var forAssigned = Assert.Single(await service.GetPendingResolutionsAsync(assignedAdmin.Auth0UserId));
        Assert.Equal(PendingMatchAssignment.Assigned, forAssigned.Assignment);
        Assert.Null(forAssigned.RequiredSinceUtc);
    }

    [Fact]
    public async Task GetPendingResolutionsAsync_WithOnlyOwnAndUnassignedMatches_NeverCallsTheAdminLookup()
    {
        var admin = CreateUser("assigned-admin");
        var player1 = CreateUser("player-one");
        var player2 = CreateUser("player-two");
        var own = CreateTournament("Own Cup", admin.Id);
        var legacy = CreateTournament("Legacy Cup", null);
        own.Matches.Add(CreatePendingMatch(own, player1, player2, bothReports: false));
        legacy.Matches.Add(CreatePendingMatch(legacy, player1, player2, bothReports: false));
        await using var dbContext = CreateDbContext();
        await SaveAsync(dbContext, [own, legacy], [admin, player1, player2]);
        var users = new[] { admin, player1, player2 };
        var identity = new CountingIdentityModule(TournamentTestSupport.CreateIdentityModule(users, adminUserIds: [admin.Id]));
        var service = CreateService(dbContext, users, identityModule: identity);

        var items = await service.GetPendingResolutionsAsync(admin.Auth0UserId);

        Assert.Equal(2, items.Count);
        Assert.Equal(0, identity.AdminLookupCount);
    }

    private static TournamentAggregate CreateTournament(string name, Guid? assignedAdminUserId) =>
        new TournamentAggregate(
            name,
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            ParticipationMode.Individual,
            null,
            NowUtc,
            30,
            10)
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Status, TournamentStatus.InProgress)
            .Set(x => x.AssignedAdminUserId, assignedAdminUserId);

    private static Match CreatePendingMatch(TournamentAggregate tournament, User player1, User player2, bool bothReports)
    {
        var match = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.LifecycleState, MatchLifecycleState.AdminResolutionRequired)
            .Set(x => x.UserParticipant1Id, player1.Id)
            .Set(x => x.UserParticipant2Id, player2.Id)
            .Set(x => x.Participant1ReportedScore1, 1)
            .Set(x => x.Participant1ReportedScore2, 0);
        return bothReports
            ? match
                .Set(x => x.Participant2ReportedScore1, 0)
                .Set(x => x.Participant2ReportedScore2, 1)
            : match;
    }

    private static MatchResolutionNotification CreateNotification(Match match, Guid adminId, DateTime occurredAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            TournamentId = match.TournamentId,
            RecipientUserId = adminId,
            RecipientKind = MatchResolutionNotificationRecipientKind.AssignedAdmin,
            OccurredAtUtc = occurredAtUtc,
            CreatedAtUtc = occurredAtUtc
        };

    private static async Task SaveAsync(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<TournamentAggregate> tournaments,
        IReadOnlyCollection<User> users)
    {
        dbContext.Users.AddRange(users);
        dbContext.Set<TournamentAggregate>().AddRange(tournaments);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    // By default every user whose name contains "admin" is a current administrator.
    private static MatchService CreateService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<User> users,
        IReadOnlyCollection<Team>? teams = null,
        IReadOnlyCollection<Guid>? adminUserIds = null,
        Exception? adminLookupFailure = null,
        CountingIdentityModule? identityModule = null,
        FixedTimeProvider? timeProvider = null,
        int escalationAfterMinutes = 10,
        TimeSpan? adminLookupTimeout = null)
    {
        var clock = timeProvider ?? new FixedTimeProvider(NowUtc);
        var identity = identityModule ?? new CountingIdentityModule(TournamentTestSupport.CreateIdentityModule(
            users,
            adminUserIds: adminUserIds ?? users.Where(user => user.Username!.Contains("admin")).Select(user => user.Id).ToList(),
            adminLookupFailure: adminLookupFailure));
        return new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            identity,
            TournamentTestSupport.CreateTeamsModule(teams, users),
            TournamentTestSupport.CreateModuleEventPublisher(),
            clock,
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)),
            Options.Create(new MatchResolutionOptions
            {
                EscalationAfterMinutes = escalationAfterMinutes,
                AdminLookupTimeout = adminLookupTimeout ?? TimeSpan.FromSeconds(3)
            }),
            new AssignedAdminAvailabilityCache(clock));
    }

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();

    private static User CreateUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{name}",
        Username = name,
        NormalizedUsername = name,
        Firstname = $"First {name}",
        Lastname = $"Last {name}"
    };

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private DateTime _utcNow = utcNow;

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => new(_utcNow);
    }

    // Forwards to the stub identity module and counts the live admin lookups.
    private sealed class CountingIdentityModule(
        IIdentityModule inner,
        Func<UserId, CancellationToken, Task<bool>>? adminLookup = null) : IIdentityModule
    {
        public int AdminLookupCount { get; private set; }

        public Task<bool> IsAdminUserAsync(UserId userId, CancellationToken cancellationToken = default)
        {
            AdminLookupCount++;
            return adminLookup is null
                ? inner.IsAdminUserAsync(userId, cancellationToken)
                : adminLookup(userId, cancellationToken);
        }

        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default) =>
            inner.GetUserProfileAsync(userId, cancellationToken);
        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            inner.GetUserProfileByAuth0IdAsync(auth0UserId, cancellationToken);
        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            inner.GetPublicProfileByUsernameAsync(username, cancellationToken);
        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default) =>
            inner.GetPublicProfileByIdAsync(userId, cancellationToken);
        public Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) =>
            inner.GetUsersByIdsAsync(userIds, cancellationToken);
        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken = default) =>
            inner.GetPublicUsernamesByIdsAsync(userIds, cancellationToken);
        public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(UserId? afterId, int pageSize, CancellationToken cancellationToken = default) =>
            inner.GetPublicUserSearchDocumentsPageAsync(afterId, pageSize, cancellationToken);
    }
}
