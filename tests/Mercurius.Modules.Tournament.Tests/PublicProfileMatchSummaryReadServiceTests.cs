using Mercurius.TestInfrastructure;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using ContractMatchLifecycleState = Mercurius.Modules.Tournament.Contracts.MatchLifecycleState;
using DomainMatchLifecycleState = Mercurius.Modules.Tournament.Domain.MatchLifecycleState;
using DomainMatchResultKind = Mercurius.Modules.Tournament.Domain.MatchResultKind;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class PublicProfileMatchSummaryReadServiceTests
{
    [Fact]
    public void CandidateProjection_SelectsOnlyPublicMatchFields()
    {
        using var dbContext = CreateTranslationDbContext();
        var projectionMethod = typeof(PublicProfileMatchSummaryReadService)
            .GetMethod("ProjectCandidateRows", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Candidate projection method was not found.");

        var rows = (IQueryable)projectionMethod.Invoke(
            null,
            [dbContext.Set<Match>().AsNoTracking()])!;
        var sql = rows.ToQueryString();

        Assert.Contains("ResultRecordedAtUtc", sql, StringComparison.Ordinal);
        Assert.Contains("Participant1Score", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Participant1ReportedScore1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Participant2ReportedScore1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultRecordedByUserId", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ScoreConfirmationDeadlineUtc", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_SelectsOneLifecycleAwareMatchPerTournament()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var tournament = CreateTournament("Alpha Cup", ParticipationMode.Individual);
        AddIndividualRegistration(tournament, userId, "profile-player");
        AddIndividualRegistration(tournament, opponentId, "public-opponent");

        tournament.Matches.Add(CreateCompletedMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            roundNumber: 1,
            matchNumber: 1));
        tournament.Matches.Add(CreateCompletedMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc),
            roundNumber: 2,
            matchNumber: 1));
        var upcomingLate = CreateUpcomingMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2099, 8, 2, 12, 0, 0, DateTimeKind.Utc),
            roundNumber: 2,
            matchNumber: 1);
        var upcomingEarly = CreateUpcomingMatch(
            tournament,
            userId,
            participant2Id: null,
            new DateTime(2099, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            roundNumber: 1,
            matchNumber: 2);
        tournament.Matches.Add(upcomingLate);
        tournament.Matches.Add(upcomingEarly);

        var canceledTournament = CreateTournament("Canceled Cup", ParticipationMode.Individual);
        canceledTournament.Set(x => x.Status, TournamentStatus.Canceled);
        AddIndividualRegistration(canceledTournament, userId, "profile-player");
        AddIndividualRegistration(canceledTournament, opponentId, "public-opponent");
        canceledTournament.Matches.Add(CreateCompletedMatch(
            canceledTournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 3, 10, 0, 0, DateTimeKind.Utc),
            roundNumber: 1,
            matchNumber: 1));

        dbContext.Set<TournamentAggregate>().AddRange(tournament, canceledTournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateService(dbContext);
        var summaries = await service.GetPublicUserMatchSummariesAsync(new UserId(userId));

        var previous = Assert.Single(summaries.PreviousMatches);
        Assert.Equal(tournament.Id, previous.TournamentId.Value);
        Assert.Equal("public-opponent", previous.OpponentDisplayName);
        Assert.Equal(1, previous.ParticipantScore);
        Assert.Equal(0, previous.OpponentScore);
        Assert.Equal(ContractMatchLifecycleState.Completed, previous.LifecycleState);
        Assert.Equal(2, previous.RoundNumber);

        var upcoming = Assert.Single(summaries.UpcomingMatches);
        Assert.Equal(upcomingEarly.Id, upcoming.MatchId.Value);
        Assert.True(upcoming.OpponentIsTbd);
        Assert.Null(upcoming.OpponentDisplayName);
        Assert.Equal(upcomingEarly.EstimatedStartTime, upcoming.EstimatedStartTime);
        Assert.Null(upcoming.ScheduledStartTime);
        Assert.Null(upcoming.StartedAtUtc);
        Assert.Equal(ContractMatchLifecycleState.AwaitingEndedConfirmation, upcoming.LifecycleState);
    }

    [Fact]
    public async Task GetPublicProfileMatchSummariesAsync_PrefersCurrentPublicLabelsOverRegistrationSnapshots()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentUserId = Guid.NewGuid();
        var individualTournament = CreateTournament("Current Player Cup", ParticipationMode.Individual);
        AddIndividualRegistration(individualTournament, userId, "profile-player");
        AddIndividualRegistration(individualTournament, opponentUserId, "stale-opponent");
        individualTournament.Matches.Add(CreateCompletedMatch(
            individualTournament,
            userId,
            opponentUserId,
            new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc)));

        var teamId = Guid.NewGuid();
        var opponentTeamId = Guid.NewGuid();
        var teamTournament = CreateTournament("Current Team Cup", ParticipationMode.Team);
        AddTeamRegistration(teamTournament, teamId, "profile-team", Guid.NewGuid());
        AddTeamRegistration(teamTournament, opponentTeamId, "Stale Team", Guid.NewGuid());
        teamTournament.Matches.Add(CreateCompletedTeamMatch(
            teamTournament,
            teamId,
            opponentTeamId,
            new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc)));

        dbContext.Set<TournamentAggregate>().AddRange(individualTournament, teamTournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var currentOpponent = new Mercurius.Modules.Identity.Domain.User
        {
            Id = opponentUserId,
            Username = "current-opponent",
            NormalizedUsername = "current-opponent",
            Firstname = "Current",
            Lastname = "Opponent"
        };
        var currentOpponentTeam = new Mercurius.Modules.Teams.Domain.Team("Current Team", Guid.NewGuid())
        {
            Id = opponentTeamId
        };

        var service = CreateService(dbContext, [currentOpponent], [currentOpponentTeam]);
        var userSummary = await service.GetPublicUserMatchSummariesAsync(new UserId(userId));
        var teamSummary = await service.GetPublicTeamMatchSummariesAsync(new TeamId(teamId));

        Assert.Equal("current-opponent", Assert.Single(userSummary.PreviousMatches).OpponentDisplayName);
        Assert.Equal("Current Team", Assert.Single(teamSummary.PreviousMatches).OpponentDisplayName);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_UsesConfirmedRosterAndCaptainFallbackForTeamMatches()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var opponentTeamId = Guid.NewGuid();
        var tournament = CreateTournament("Team Cup", ParticipationMode.Team);
        var ownRegistration = AddTeamRegistration(tournament, teamId, "Roster Team", captainId: Guid.NewGuid());
        ownRegistration.RosterMembers.Add(new TournamentRegistrationRosterMember
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            TournamentRegistration = ownRegistration,
            TournamentRegistrationId = ownRegistration.Id,
            TeamId = teamId,
            UserId = userId,
            UsernameAtRegistration = "roster-player",
            DisplayNameAtRegistration = "Roster Player",
            ConfirmationStatus = RosterMemberConfirmationStatus.Confirmed
        });
        AddTeamRegistration(tournament, opponentTeamId, "Opponent Team", captainId: Guid.NewGuid());
        var teamMatch = CreateCompletedTeamMatch(
            tournament,
            teamId,
            opponentTeamId,
            new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc));
        tournament.Matches.Add(teamMatch);

        var captainId = Guid.NewGuid();
        var captainTeamId = Guid.NewGuid();
        var captainTournament = CreateTournament("Captain Cup", ParticipationMode.Team);
        AddTeamRegistration(captainTournament, captainTeamId, "Captain Snapshot", captainId);
        AddTeamRegistration(captainTournament, opponentTeamId, "Other Snapshot", Guid.NewGuid());
        captainTournament.Matches.Add(CreateCompletedTeamMatch(
            captainTournament,
            captainTeamId,
            opponentTeamId,
            new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc)));

        dbContext.Set<TournamentAggregate>().AddRange(tournament, captainTournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateService(dbContext);
        var rosterSummaries = await service.GetPublicUserMatchSummariesAsync(new UserId(userId));
        var captainSummaries = await service.GetPublicUserMatchSummariesAsync(new UserId(captainId));
        var teamSummaries = await service.GetPublicTeamMatchSummariesAsync(new TeamId(teamId));

        Assert.Equal(teamMatch.Id, Assert.Single(rosterSummaries.PreviousMatches).MatchId.Value);
        Assert.Equal("Opponent Team", rosterSummaries.PreviousMatches[0].OpponentDisplayName);
        Assert.Equal("Other Snapshot", Assert.Single(captainSummaries.PreviousMatches).OpponentDisplayName);
        Assert.Equal(teamMatch.Id, Assert.Single(teamSummaries.PreviousMatches).MatchId.Value);
    }

    [Fact]
    public async Task GetPublicProfileMatchSummariesAsync_UsesRetainedOpponentSnapshotsAfterDeactivation()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentUserId = Guid.NewGuid();
        var individualTournament = CreateTournament("Historical Player Cup", ParticipationMode.Individual);
        AddIndividualRegistration(individualTournament, userId, "profile-player");
        var removedPlayer = AddIndividualRegistration(individualTournament, opponentUserId, "removed-opponent");
        removedPlayer.Status = TournamentRegistrationStatus.PendingConfirmation;
        individualTournament.Matches.Add(CreateCompletedMatch(
            individualTournament,
            userId,
            opponentUserId,
            new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc)));

        var teamId = Guid.NewGuid();
        var opponentTeamId = Guid.NewGuid();
        var teamTournament = CreateTournament("Historical Team Cup", ParticipationMode.Team);
        AddTeamRegistration(teamTournament, teamId, "Profile Team", Guid.NewGuid());
        var removedTeam = AddTeamRegistration(teamTournament, opponentTeamId, "Removed Team", Guid.NewGuid());
        removedTeam.Status = TournamentRegistrationStatus.PendingConfirmation;
        teamTournament.Matches.Add(CreateCompletedTeamMatch(
            teamTournament,
            teamId,
            opponentTeamId,
            new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc)));

        dbContext.Set<TournamentAggregate>().AddRange(individualTournament, teamTournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateService(dbContext);
        var userSummaries = await service.GetPublicUserMatchSummariesAsync(new UserId(userId));
        var teamSummaries = await service.GetPublicTeamMatchSummariesAsync(new TeamId(teamId));

        Assert.Equal("removed-opponent", Assert.Single(userSummaries.PreviousMatches).OpponentDisplayName);
        Assert.Equal("Removed Team", Assert.Single(teamSummaries.PreviousMatches).OpponentDisplayName);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_ExcludesStartedMatchesButKeepsDelayedAndUnscheduledMatches()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var tournament = CreateTournament("Delayed Cup", ParticipationMode.Individual);
        AddIndividualRegistration(tournament, userId, "profile-player");
        AddIndividualRegistration(tournament, opponentId, "opponent");

        var started = CreateUpcomingMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2099, 8, 10, 10, 0, 0, DateTimeKind.Utc),
            roundNumber: 1,
            matchNumber: 1);
        started.Set(x => x.StartTime, new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc));

        var delayed = CreateUpcomingMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 10, 8, 0, 0, DateTimeKind.Utc),
            roundNumber: 1,
            matchNumber: 2);
        var unscheduledTournament = CreateTournament("Unscheduled Cup", ParticipationMode.Individual);
        AddIndividualRegistration(unscheduledTournament, userId, "profile-player");
        AddIndividualRegistration(unscheduledTournament, opponentId, "opponent");
        var unscheduled = CreateUpcomingMatch(
            unscheduledTournament,
            userId,
            opponentId,
            new DateTime(2099, 8, 11, 8, 0, 0, DateTimeKind.Utc));
        unscheduled.Set(x => x.EstimatedStartTime, null);
        unscheduled.Set(x => x.EstimatedEndTime, null);

        tournament.Matches.Add(started);
        tournament.Matches.Add(delayed);
        unscheduledTournament.Matches.Add(unscheduled);
        dbContext.Set<TournamentAggregate>().AddRange(tournament, unscheduledTournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var summaries = await CreateService(dbContext)
            .GetPublicUserMatchSummariesAsync(new UserId(userId));

        var delayedSummary = Assert.Single(summaries.UpcomingMatches, summary => summary.TournamentName == "Delayed Cup");
        Assert.Equal(delayed.Id, delayedSummary.MatchId.Value);
        Assert.Equal(delayed.EstimatedStartTime, delayedSummary.EstimatedStartTime);
        Assert.Null(delayedSummary.ScheduledStartTime);

        var unscheduledSummary = Assert.Single(summaries.UpcomingMatches, summary => summary.TournamentName == "Unscheduled Cup");
        Assert.Equal(unscheduled.Id, unscheduledSummary.MatchId.Value);
        Assert.Null(unscheduledSummary.EstimatedStartTime);
        Assert.Null(unscheduledSummary.ScheduledStartTime);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_ExcludesByeReversedAndUnresolvedMatches()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var tournament = CreateTournament("State Cup", ParticipationMode.Individual);
        AddIndividualRegistration(tournament, userId, "profile-player");
        AddIndividualRegistration(tournament, opponentId, "opponent");

        var reversed = CreateCompletedMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        reversed.Set(x => x.LifecycleState, DomainMatchLifecycleState.Reversed);
        var unresolved = CreateUpcomingMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2099, 8, 2, 10, 0, 0, DateTimeKind.Utc));
        unresolved.Set(x => x.LifecycleState, DomainMatchLifecycleState.Disputed);
        var bye = CreateUpcomingMatch(
            tournament,
            userId,
            participant2Id: null,
            new DateTime(2099, 8, 1, 10, 0, 0, DateTimeKind.Utc));
        bye.Set(x => x.Participant1IsBYE, true);
        tournament.Matches.Add(reversed);
        tournament.Matches.Add(unresolved);
        tournament.Matches.Add(bye);

        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();

        var summaries = await CreateService(dbContext)
            .GetPublicUserMatchSummariesAsync(new UserId(userId));

        Assert.Empty(summaries.PreviousMatches);
        Assert.Empty(summaries.UpcomingMatches);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_IncludesForfeitWithoutScoreFields()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var tournament = CreateTournament("Forfeit Cup", ParticipationMode.Individual);
        AddIndividualRegistration(tournament, userId, "profile-player");
        AddIndividualRegistration(tournament, opponentId, "public-opponent");

        var forfeited = CreateCompletedMatch(
            tournament,
            userId,
            opponentId,
            new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc));
        forfeited.Set(x => x.LifecycleState, DomainMatchLifecycleState.Forfeited);
        forfeited.Set(x => x.ResultKind, DomainMatchResultKind.Forfeit);
        forfeited.Set(x => x.Participant1Score, null);
        forfeited.Set(x => x.Participant2Score, null);
        tournament.Matches.Add(forfeited);

        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var summarySet = await CreateService(dbContext)
            .GetPublicUserMatchSummariesAsync(new UserId(userId));
        var summary = Assert.Single(summarySet.PreviousMatches);

        Assert.Equal(ContractMatchLifecycleState.Forfeited, summary.LifecycleState);
        Assert.Equal(Mercurius.Modules.Tournament.Contracts.MatchResultKind.Forfeit, summary.ResultKind);
        Assert.Null(summary.ParticipantScore);
        Assert.Null(summary.OpponentScore);
        Assert.Equal("public-opponent", summary.OpponentDisplayName);
    }

    [Fact]
    public async Task GetPublicUserMatchSummariesAsync_LargeHistoryRemainsBoundedPerTournament()
    {
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var opponentId = Guid.NewGuid();
        var tournament = CreateTournament("Large History Cup", ParticipationMode.Individual);
        AddIndividualRegistration(tournament, userId, "profile-player");
        AddIndividualRegistration(tournament, opponentId, "public-opponent");

        for (var index = 0; index < 250; index++)
        {
            tournament.Matches.Add(CreateCompletedMatch(
                tournament,
                userId,
                opponentId,
                new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                roundNumber: index + 1,
                matchNumber: 1));
        }

        for (var index = 0; index < 250; index++)
        {
            tournament.Matches.Add(CreateUpcomingMatch(
                tournament,
                userId,
                opponentId,
                new DateTime(2099, 8, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                roundNumber: index + 1,
                matchNumber: 1));
        }

        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var summaries = await CreateService(dbContext)
            .GetPublicUserMatchSummariesAsync(new UserId(userId));

        Assert.Single(summaries.PreviousMatches);
        Assert.Single(summaries.UpcomingMatches);
        Assert.Equal(250, summaries.PreviousMatches[0].RoundNumber);
        Assert.Equal(1, summaries.UpcomingMatches[0].RoundNumber);
    }

    private static PublicProfileMatchSummaryReadService CreateService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<Modules.Identity.Domain.User>? users = null,
        IReadOnlyCollection<Modules.Teams.Domain.Team>? teams = null) =>
        new(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(users),
            TournamentTestSupport.CreateTeamsModule(teams, users));

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MercuriusDBContext(options);
    }

    private static MercuriusDBContext CreateTranslationDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=translation-only")
            .Options;
        return new MercuriusDBContext(options);
    }

    private static TournamentAggregate CreateTournament(string name, ParticipationMode mode)
    {
        return new TournamentAggregate(
            name,
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf3,
            mode,
            mode == ParticipationMode.Team ? 2 : null,
            DateTime.UtcNow,
            30,
            10).Set(x => x.Id, Guid.NewGuid());
    }

    private static TournamentRegistration AddIndividualRegistration(
        TournamentAggregate tournament,
        Guid userId,
        string username)
    {
        var registration = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = userId,
            RegisteredByUsernameAtRegistration = username,
            UserId = userId,
            UsernameAtRegistration = username
        };
        tournament.TournamentRegistrations.Add(registration);
        return registration;
    }

    private static TournamentRegistration AddTeamRegistration(
        TournamentAggregate tournament,
        Guid teamId,
        string teamName,
        Guid captainId)
    {
        var registration = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = captainId,
            RegisteredByUsernameAtRegistration = "captain",
            TeamId = teamId,
            TeamNameAtRegistration = teamName,
            TeamCaptainUserIdAtRegistration = captainId
        };
        tournament.TournamentRegistrations.Add(registration);
        return registration;
    }

    private static Match CreateCompletedMatch(
        TournamentAggregate tournament,
        Guid participant1Id,
        Guid participant2Id,
        DateTime resultRecordedAtUtc,
        int roundNumber = 1,
        int matchNumber = 1)
    {
        return new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Tournament, tournament)
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id)
            .Set(x => x.Participant1Score, 1)
            .Set(x => x.Participant2Score, 0)
            .Set(x => x.StartTime, resultRecordedAtUtc.AddMinutes(-30))
            .Set(x => x.EndTime, resultRecordedAtUtc)
            .Set(x => x.ResultRecordedAtUtc, resultRecordedAtUtc)
            .Set(x => x.ResultKind, DomainMatchResultKind.Score)
            .Set(x => x.LifecycleState, DomainMatchLifecycleState.Completed)
            .Set(x => x.RoundNumber, roundNumber)
            .Set(x => x.MatchNumber, matchNumber);
    }

    private static Match CreateCompletedTeamMatch(
        TournamentAggregate tournament,
        Guid participant1Id,
        Guid participant2Id,
        DateTime resultRecordedAtUtc)
    {
        var match = CreateCompletedMatch(tournament, participant1Id, participant2Id, resultRecordedAtUtc);
        match.Set(x => x.ParticipationMode, ParticipationMode.Team);
        match.Set(x => x.UserParticipant1Id, null);
        match.Set(x => x.UserParticipant2Id, null);
        match.Set(x => x.TeamParticipant1Id, participant1Id);
        match.Set(x => x.TeamParticipant2Id, participant2Id);
        return match;
    }

    private static Match CreateUpcomingMatch(
        TournamentAggregate tournament,
        Guid participant1Id,
        Guid? participant2Id,
        DateTime estimatedStartTime,
        int roundNumber = 1,
        int matchNumber = 1)
    {
        return new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.Tournament, tournament)
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.Format, GameFormat.BestOf1)
            .Set(x => x.UserParticipant1Id, participant1Id)
            .Set(x => x.UserParticipant2Id, participant2Id)
            .Set(x => x.EstimatedStartTime, estimatedStartTime)
            .Set(x => x.EstimatedEndTime, estimatedStartTime.AddMinutes(30))
            .Set(x => x.StartTime, DateTime.MinValue)
            .Set(x => x.EndTime, DateTime.MinValue)
            .Set(x => x.LifecycleState, DomainMatchLifecycleState.AwaitingEndedConfirmation)
            .Set(x => x.RoundNumber, roundNumber)
            .Set(x => x.MatchNumber, matchNumber);
    }
}
