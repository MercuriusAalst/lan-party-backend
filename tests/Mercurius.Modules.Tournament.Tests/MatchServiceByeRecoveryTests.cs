using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Tournament.Application.DTOs.Matches;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class MatchServiceByeRecoveryTests
{
    [Fact]
    public async Task RecoverByeAdvancementAsync_PropagatesThroughChainAndIsIdempotent()
    {
        var admin = CreateUser("bye-recovery-admin");
        var tournament = CreateTournament(admin);
        var participant = Guid.NewGuid();
        var finalOpponent = Guid.NewGuid();
        var recordedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = CreateLowerByeMatch(tournament, 1, participant, participant2IsBye: true)
            .Set(x => x.UserWinnerId, participant)
            .Set(x => x.LifecycleState, MatchLifecycleState.Completed)
            .Set(x => x.ResultKind, MatchResultKind.Score)
            .Set(x => x.ResultRecordedAtUtc, recordedAtUtc);
        var second = CreateLowerByeMatch(tournament, 2, null, participant1IsBye: true);
        var final = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.UserParticipant1Id, finalOpponent);
        first.SetWinnerNextMatch(second);
        second.SetWinnerNextMatch(final);
        tournament.Matches.Add(first);
        tournament.Matches.Add(second);
        tournament.Matches.Add(final);

        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        dbContext.AddReferencedParticipants(tournament);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext, admin);

        var firstRecovery = await service.RecoverByeAdvancementAsync(tournament.Id);
        var repeatedRecovery = await service.RecoverByeAdvancementAsync(tournament.Id);

        Assert.True(firstRecovery.MatchesUpdated > 0);
        Assert.Equal(0, repeatedRecovery.MatchesUpdated);
        var saved = await dbContext.Set<Match>().AsNoTracking().ToListAsync();
        Assert.Equal(participant, saved.Single(match => match.Id == first.Id).UserWinnerId);
        Assert.Equal(participant, saved.Single(match => match.Id == second.Id).UserWinnerId);
        Assert.Equal(participant, saved.Single(match => match.Id == final.Id).UserParticipant2Id);
        Assert.Equal(MatchLifecycleState.Completed, saved.Single(match => match.Id == first.Id).LifecycleState);
        Assert.Equal(MatchResultKind.Score, saved.Single(match => match.Id == first.Id).ResultKind);
        Assert.Equal(recordedAtUtc, saved.Single(match => match.Id == first.Id).ResultRecordedAtUtc);
        Assert.Equal(MatchLifecycleState.AwaitingEndedConfirmation, saved.Single(match => match.Id == final.Id).LifecycleState);
    }

    [Fact]
    public async Task RecoverByeAdvancementAsync_ConflictDoesNotPersistEarlierAssignments()
    {
        var admin = CreateUser("bye-recovery-conflict-admin");
        var tournament = CreateTournament(admin);
        var participant = Guid.NewGuid();
        var conflictingParticipant = Guid.NewGuid();
        var first = CreateLowerByeMatch(tournament, 1, participant, participant2IsBye: true);
        var target = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.UserParticipant2Id, conflictingParticipant);
        first.SetWinnerNextMatch(target);
        tournament.Matches.Add(first);
        tournament.Matches.Add(target);

        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        dbContext.AddReferencedParticipants(tournament);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext, admin);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.RecoverByeAdvancementAsync(tournament.Id));

        Assert.Equal("bracket_advancement_conflict", exception.Code);
        dbContext.ChangeTracker.Clear();
        var savedFirst = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == first.Id);
        var savedTarget = await dbContext.Set<Match>().AsNoTracking().SingleAsync(match => match.Id == target.Id);
        Assert.Null(savedFirst.UserWinnerId);
        Assert.Equal(conflictingParticipant, savedTarget.UserParticipant2Id);
    }

    [Fact]
    public async Task ForfeitAsync_PropagatesLowerByeWinnerThroughPersistedMatchGraph()
    {
        var admin = CreateUser("bye-runtime-admin");
        var p1 = CreateUser("bye-runtime-p1");
        var p2 = CreateUser("bye-runtime-p2");
        var tournament = CreateTournament(admin);
        var upper = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.RoundNumber, 1)
            .Set(x => x.MatchNumber, 1)
            .Set(x => x.UserParticipant1Id, p1.Id)
            .Set(x => x.UserParticipant2Id, p2.Id);
        var lowerBye = CreateLowerByeMatch(tournament, 1, null, participant2IsBye: true);
        var chainedBye = CreateLowerByeMatch(tournament, 2, null, participant1IsBye: true);
        var final = new Match()
            .Set(x => x.Id, Guid.NewGuid())
            .Set(x => x.TournamentId, tournament.Id)
            .Set(x => x.Tournament, tournament)
            .Set(x => x.ParticipationMode, ParticipationMode.Individual)
            .Set(x => x.UserParticipant1Id, Guid.NewGuid());
        upper.SetLoserNextMatch(lowerBye);
        lowerBye.SetWinnerNextMatch(chainedBye);
        chainedBye.SetWinnerNextMatch(final);
        tournament.Matches.Add(upper);
        tournament.Matches.Add(lowerBye);
        tournament.Matches.Add(chainedBye);
        tournament.Matches.Add(final);

        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        dbContext.AddReferencedParticipants(tournament);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var service = CreateService(dbContext, admin, p1, p2);

        await service.ForfeitAsync(
            upper.Id,
            admin.Auth0UserId,
            new ForfeitMatchDTO { Participant = Mercurius.Modules.Tournament.Contracts.MatchParticipantSide.Participant1 },
            isAdmin: true);

        dbContext.ChangeTracker.Clear();
        var saved = await dbContext.Set<Match>().AsNoTracking().ToListAsync();
        Assert.Equal(p1.Id, saved.Single(match => match.Id == lowerBye.Id).UserWinnerId);
        Assert.Equal(p1.Id, saved.Single(match => match.Id == chainedBye.Id).UserWinnerId);
        Assert.Equal(p1.Id, saved.Single(match => match.Id == final.Id).UserParticipant2Id);
    }

    private static TournamentAggregate CreateTournament(User admin) => new TournamentAggregate(
        "BYE recovery",
        BracketType.DoubleElimination,
        GameFormat.BestOf1,
        GameFormat.BestOf1,
        ParticipationMode.Individual,
        null,
        DateTime.UtcNow,
        30,
        10)
        .Set(x => x.Id, Guid.NewGuid())
        .Set(x => x.Status, TournamentStatus.InProgress)
        .Set(x => x.AssignedAdminUserId, admin.Id);

    private static Match CreateLowerByeMatch(
        TournamentAggregate tournament,
        int round,
        Guid? participant1,
        bool participant1IsBye = false,
        bool participant2IsBye = false) => new Match()
        .Set(x => x.Id, Guid.NewGuid())
        .Set(x => x.TournamentId, tournament.Id)
        .Set(x => x.Tournament, tournament)
        .Set(x => x.ParticipationMode, ParticipationMode.Individual)
        .Set(x => x.IsLowerBracketMatch, true)
        .Set(x => x.RoundNumber, round)
        .Set(x => x.UserParticipant1Id, participant1)
        .Set(x => x.Participant1IsBYE, participant1IsBye)
        .Set(x => x.Participant2IsBYE, participant2IsBye);

    private static MatchService CreateService(MercuriusDBContext dbContext, params User[] users)
    {
        var adapter = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        return new MatchService(
            adapter,
            TournamentTestSupport.CreateIdentityModule(users),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            TimeProvider.System,
            new MatchBracketImpactAnalyzer(adapter));
    }

    private static User CreateUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Auth0UserId = $"auth0|{name}",
        Username = name
    };
}
