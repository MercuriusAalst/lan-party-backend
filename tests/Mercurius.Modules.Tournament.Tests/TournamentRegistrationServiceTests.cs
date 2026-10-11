using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Application.DTOs.Registrations;
using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Services;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Eventing;
using TournamentRegistrationStatus = Mercurius.Modules.Tournament.Contracts.TournamentRegistrationStatus;
using TournamentRosterStatus = Mercurius.Modules.Tournament.Contracts.RosterMemberConfirmationStatus;
using DomainTournamentRegistrationStatus = Mercurius.Modules.Tournament.Domain.TournamentRegistrationStatus;

namespace Mercurius.Modules.Tournament.Tests;

public class TournamentRegistrationServiceTests
{

    [Fact]
    public async Task RegisterIndividualAsync_CreatesActiveRegistrationAndBlocksDuplicate()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("solo");
        var tournament = CreateIndividualTournament();
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var registration = await service.RegisterIndividualAsync(user.Auth0UserId, tournament.Id);

        Assert.Equal(TournamentRegistrationStatus.Active, registration.Status);
        Assert.Equal(user.Id, registration.User!.Id);
        var duplicate = await Assert.ThrowsAsync<ValidationException>(() => service.RegisterIndividualAsync(user.Auth0UserId, tournament.Id));
        Assert.Contains("duplicate_participation", duplicate.Message);
    }

    [Fact]
    public async Task UnregisterIndividualAsync_RemovesUserFromActiveParticipation()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("solo");
        var tournament = CreateIndividualTournament();
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        await service.RegisterIndividualAsync(user.Auth0UserId, tournament.Id);

        await service.UnregisterIndividualAsync(user.Auth0UserId, tournament.Id);

        Assert.False(await dbContext.Set<TournamentRegistration>().AnyAsync());
        var eligibility = await service.CheckIndividualEligibilityAsync(user.Auth0UserId, tournament.Id);
        Assert.True(eligibility.Eligible);
    }

    [Fact]
    public async Task UnregisterIndividualAsync_RejectsStartedTournament()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("solo");
        var tournament = CreateIndividualTournament();
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        await service.RegisterIndividualAsync(user.Auth0UserId, tournament.Id);
        tournament.Status = TournamentStatus.InProgress;
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.UnregisterIndividualAsync(user.Auth0UserId, tournament.Id));

        Assert.Contains("scheduled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await dbContext.Set<TournamentRegistration>().AnyAsync(registration => registration.TournamentId == tournament.Id && registration.UserId == user.Id));
    }

    [Fact]
    public async Task RegisterIndividualAsync_BlocksPendingRosterParticipation()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var teamTournament = CreateTeamTournament(teamSize: 2);
        var individualTournament = CreateIndividualTournament();
        individualTournament.Id = teamTournament.Id;
        individualTournament.ParticipationMode = ParticipationMode.Individual;
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(individualTournament);
        AddTeamRegistration(
            dbContext,
            individualTournament,
            team,
            captain,
            [captain, member],
            Mercurius.Modules.Tournament.Domain.TournamentRegistrationStatus.PendingConfirmation);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => service.RegisterIndividualAsync(member.Auth0UserId, individualTournament.Id));

        Assert.Contains("duplicate_participation", exception.Message);
    }

    [Fact]
    public async Task RegistrationMutations_AreSpecificToTournamentParticipationMode()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var individualTournament = CreateIndividualTournament();
        var teamTournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().AddRange(individualTournament, teamTournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var individualOnTeam = await Assert.ThrowsAsync<ValidationException>(() => service.RegisterIndividualAsync(captain.Auth0UserId, teamTournament.Id));
        Assert.Contains("not_individual_tournament", individualOnTeam.Message);

        var teamOnIndividualEligibility = await service.CheckTeamEligibilityAsync(captain.Auth0UserId, individualTournament.Id, team.Id);
        Assert.False(teamOnIndividualEligibility.Eligible);
        Assert.Contains("not_team_tournament", teamOnIndividualEligibility.ReasonCodes);

        var teamOnIndividual = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SubmitTeamRosterAsync(captain.Auth0UserId, individualTournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id])));
        Assert.Contains("not_team_tournament", teamOnIndividual.Message);
    }

    [Fact]
    public async Task GetCurrentUserStateAsync_DoesNotOfferIndividualRegistrationForTeamTournament()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("captain");
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var state = await service.GetCurrentUserStateAsync(user.Auth0UserId, tournament.Id);

        Assert.False(state.CanRegisterIndividual);
    }

    [Fact]
    public async Task GetCurrentUserStateAsync_ReturnsPendingConfirmationAndCaptainManagedRegistration()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));

        var memberState = await service.GetCurrentUserStateAsync(member.Auth0UserId, tournament.Id);
        var captainState = await service.GetCurrentUserStateAsync(captain.Auth0UserId, tournament.Id);

        Assert.True(memberState.CanConfirmRoster);
        Assert.NotNull(memberState.PendingRosterConfirmation);
        Assert.Equal(TournamentRosterStatus.Pending, memberState.PendingRosterConfirmation.ConfirmationStatus);
        Assert.False(memberState.CanRegisterIndividual);
        Assert.Contains(captainState.CaptainManagedRegistrations, registration => registration.Id == pending.Id);
        Assert.True(captainState.CanUnregister);
    }

    [Fact]
    public async Task GetCurrentUserStateAsync_PreservesConfirmedMemberContextWhileAnotherMemberIsPending()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var confirmedMember = CreateUser("confirmed");
        var pendingMember = CreateUser("pending");
        var team = CreateTeam(captain, confirmedMember, pendingMember);
        var tournament = CreateTeamTournament(teamSize: 3);
        dbContext.Users.AddRange(captain, confirmedMember, pendingMember);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, confirmedMember.Id, pendingMember.Id]));
        var confirmedRosterMember = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == confirmedMember.Id));

        await service.ConfirmRosterAsync(confirmedMember.Auth0UserId, tournament.Id, confirmedRosterMember.Id);

        var state = await service.GetCurrentUserStateAsync(confirmedMember.Auth0UserId, tournament.Id);

        var currentTeam = state.CurrentTeamRegistration;
        Assert.NotNull(currentTeam);
        Assert.Equal(pending.Id, currentTeam.Id);
        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, currentTeam.Status);
        Assert.Equal(team.Id, currentTeam.Team!.Id);
        Assert.Contains(currentTeam.RosterMembers, roster =>
            roster.User.Id == confirmedMember.Id && roster.ConfirmationStatus == TournamentRosterStatus.Confirmed);
        Assert.Contains(currentTeam.RosterMembers, roster =>
            roster.User.Id == pendingMember.Id && roster.ConfirmationStatus == TournamentRosterStatus.Pending);
        Assert.Null(state.ActiveTeamRegistration);
        Assert.False(state.CanConfirmRoster);
        Assert.False(state.CanUnregister);
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_PublishesPendingRosterEventsAndConfirmingActivatesTeam()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var publisher = TournamentTestSupport.CreateRealtimePublisher();
        var service = CreateService(dbContext, publisher);

        var pending = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));

        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, pending.Status);
        Assert.Contains(pending.RosterMembers, roster => roster.User.Id == captain.Id && roster.ConfirmationStatus == TournamentRosterStatus.AutoConfirmed);
        var memberRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == member.Id));
        Assert.Equal(TournamentRosterStatus.Pending, memberRoster.ConfirmationStatus);
        Assert.Empty(await dbContext.Set<TeamInvite>().ToListAsync());
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(roster =>
            roster.UserId == member.Id &&
            roster.ConfirmationStatus == RosterMemberConfirmationStatus.Pending));
        Assert.Contains(publisher.Events, evt => evt.TeamId == team.Id && evt.UserId == member.Id && evt.Status == nameof(RosterMemberConfirmationStatus.Pending));

        var active = await service.ConfirmRosterAsync(member.Auth0UserId, tournament.Id, memberRoster.Id);

        Assert.Equal(TournamentRegistrationStatus.Active, active.Status);
        Assert.Contains(active.RosterMembers, roster => roster.User.Id == member.Id && roster.ConfirmationStatus == TournamentRosterStatus.Confirmed);
        Assert.Contains(publisher.Events, evt => evt.TeamId == team.Id && evt.UserId == member.Id && evt.Status == nameof(RosterMemberConfirmationStatus.Confirmed));
    }

    [Fact]
    public async Task DeclineRosterAsync_RemovesOnlyOwnedPendingSelectionAndIsIdempotent()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var confirmed = CreateUser("confirmed");
        var declining = CreateUser("declining");
        var outsider = CreateUser("outsider");
        var team = CreateTeam(captain, confirmed, declining);
        var tournament = CreateTeamTournament(teamSize: 3);
        dbContext.Users.AddRange(captain, confirmed, declining, outsider);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var publisher = TournamentTestSupport.CreateRealtimePublisher();
        var service = CreateService(dbContext, publisher);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, confirmed.Id, declining.Id]));
        var confirmedRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == confirmed.Id));
        var decliningRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == declining.Id));
        await service.ConfirmRosterAsync(confirmed.Auth0UserId, tournament.Id, confirmedRoster.Id);

        await service.DeclineRosterAsync(outsider.Auth0UserId, tournament.Id, decliningRoster.Id);
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(member => member.Id == decliningRoster.Id));

        await service.DeclineRosterAsync(declining.Auth0UserId, tournament.Id, decliningRoster.Id);
        await service.DeclineRosterAsync(declining.Auth0UserId, tournament.Id, decliningRoster.Id);

        dbContext.ChangeTracker.Clear();
        var registration = await dbContext.Set<TournamentRegistration>()
            .Include(item => item.RosterMembers)
            .SingleAsync(item => item.Id == pending.Id);
        Assert.Equal(DomainTournamentRegistrationStatus.PendingConfirmation, registration.Status);
        Assert.DoesNotContain(registration.RosterMembers, member => member.UserId == declining.Id);
        Assert.Contains(registration.RosterMembers, member =>
            member.UserId == confirmed.Id && member.ConfirmationStatus == RosterMemberConfirmationStatus.Confirmed);
        Assert.Contains(publisher.Events, evt => evt.UserId == declining.Id && evt.Status == "Declined");
    }

    [Fact]
    public async Task ConfirmRosterAsync_SucceedsWhenPostCommitRealtimePublishingFails()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        var selectedMember = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == member.Id));
        service = CreateService(dbContext, new ThrowingTournamentRealtimePublisher());

        var result = await service.ConfirmRosterAsync(member.Auth0UserId, tournament.Id, selectedMember.Id);

        Assert.Equal(TournamentRegistrationStatus.Active, result.Status);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            RosterMemberConfirmationStatus.Confirmed,
            (await dbContext.Set<TournamentRegistrationRosterMember>().SingleAsync(roster => roster.Id == selectedMember.Id)).ConfirmationStatus);
    }

    [Fact]
    public async Task DeclineRosterAsync_SucceedsWhenPostCommitRealtimePublishingFails()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        var selectedMember = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == member.Id));
        service = CreateService(dbContext, new ThrowingTournamentRealtimePublisher());

        await service.DeclineRosterAsync(member.Auth0UserId, tournament.Id, selectedMember.Id);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(roster => roster.Id == selectedMember.Id));
        Assert.Equal(
            DomainTournamentRegistrationStatus.PendingConfirmation,
            (await dbContext.Set<TournamentRegistration>().SingleAsync(registration => registration.Id == pending.Id)).Status);
    }

    [Fact]
    public async Task ConfirmRosterAsync_DoesNotActivateAnIncompleteRoster()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var confirming = CreateUser("confirming");
        var declining = CreateUser("declining");
        var team = CreateTeam(captain, confirming, declining);
        var tournament = CreateTeamTournament(teamSize: 3);
        dbContext.Users.AddRange(captain, confirming, declining);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, confirming.Id, declining.Id]));
        var confirmingRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == confirming.Id));
        var decliningRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == declining.Id));

        await service.DeclineRosterAsync(declining.Auth0UserId, tournament.Id, decliningRoster.Id);
        var result = await service.ConfirmRosterAsync(confirming.Auth0UserId, tournament.Id, confirmingRoster.Id);

        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, result.Status);
        Assert.Equal(2, result.RosterMembers.Count);
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_RepairsDeclinedRosterWithoutResettingConfirmations()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var confirmed = CreateUser("confirmed");
        var declining = CreateUser("declining");
        var replacement = CreateUser("replacement");
        var team = CreateTeam(captain, confirmed, declining, replacement);
        var tournament = CreateTeamTournament(teamSize: 3);
        dbContext.Users.AddRange(captain, confirmed, declining, replacement);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var publisher = TournamentTestSupport.CreateRealtimePublisher();
        var service = CreateService(dbContext, publisher);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, confirmed.Id, declining.Id]));
        var confirmedRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == confirmed.Id));
        var decliningRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == declining.Id));
        await service.ConfirmRosterAsync(confirmed.Auth0UserId, tournament.Id, confirmedRoster.Id);
        await service.DeclineRosterAsync(declining.Auth0UserId, tournament.Id, decliningRoster.Id);
        publisher.Events.Clear();

        var repaired = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, confirmed.Id, replacement.Id]));

        var preserved = Assert.Single(repaired.RosterMembers.Where(roster => roster.User.Id == confirmed.Id));
        Assert.Equal(confirmedRoster.Id, preserved.Id);
        Assert.Equal(TournamentRosterStatus.Confirmed, preserved.ConfirmationStatus);
        Assert.Contains(publisher.Events, evt => evt.UserId == replacement.Id && evt.Status == nameof(RosterMemberConfirmationStatus.Pending));
        Assert.DoesNotContain(publisher.Events, evt => evt.UserId == confirmed.Id);
    }

    [Fact]
    public async Task GetPendingRosterConfirmationsAsync_ReturnsPagedScheduledSelections()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var newestTournament = CreateTeamTournament(teamSize: 2);
        newestTournament.Name = "Newest Cup";
        var olderTournament = CreateTeamTournament(teamSize: 2);
        olderTournament.Name = "Older Cup";
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().AddRange(newestTournament, olderTournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        await service.SubmitTeamRosterAsync(captain.Auth0UserId, olderTournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        await service.SubmitTeamRosterAsync(captain.Auth0UserId, newestTournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));

        var firstPage = await service.GetPendingRosterConfirmationsAsync(member.Auth0UserId, page: 1, pageSize: 1);
        newestTournament.Status = TournamentStatus.Canceled;
        await dbContext.SaveChangesAsync();
        var afterCancellation = await service.GetPendingRosterConfirmationsAsync(member.Auth0UserId, page: 1, pageSize: 20);

        Assert.Equal(2, firstPage.TotalCount);
        Assert.Single(firstPage.Items);
        Assert.Equal("Newest Cup", firstPage.Items[0].TournamentName);
        Assert.Equal(team.Name, firstPage.Items[0].TeamName);
        Assert.Equal(1, afterCancellation.TotalCount);
        Assert.Equal("Older Cup", Assert.Single(afterCancellation.Items).TournamentName);
    }

    [Fact]
    public async Task RegistrationPersistence_RejectsAStaleConcurrentRosterMutation()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        var connectionString = dbContext.Database.GetConnectionString();
        dbContext.ChangeTracker.Clear();
        await using var competingContext = new MercuriusDBContext(
            new DbContextOptionsBuilder<MercuriusDBContext>()
                .UseNpgsql(connectionString)
                .Options);
        var first = await dbContext.Set<TournamentRegistration>().SingleAsync(item => item.Id == pending.Id);
        var stale = await competingContext.Set<TournamentRegistration>().SingleAsync(item => item.Id == pending.Id);
        first.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        stale.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(2);
        var firstCoordinator = new TournamentRegistrationPersistenceCoordinator(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext));
        var staleCoordinator = new TournamentRegistrationPersistenceCoordinator(
            new TournamentDbContextAdapter<MercuriusDBContext>(competingContext));

        await firstCoordinator.SaveChangesAsync("duplicate", CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            staleCoordinator.SaveChangesAsync("duplicate", CancellationToken.None));
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_ValidatesCaptainExactSizeMembershipAndDuplicates()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var outsider = CreateUser("outsider");
        var registeredElsewhere = CreateUser("registered");
        var team = CreateTeam(captain, member, registeredElsewhere);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member, outsider, registeredElsewhere);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, registeredElsewhere);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var nonCaptain = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SubmitTeamRosterAsync(member.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id])));
        var missingCaptain = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [member.Id, outsider.Id])));
        var wrongSize = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id])));
        var duplicate = await Assert.ThrowsAsync<ValidationException>(() =>
            service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, registeredElsewhere.Id])));

        Assert.Contains("captain_required", nonCaptain.Message);
        Assert.Contains("captain_required", missingCaptain.Message);
        Assert.Contains("not_team_member", missingCaptain.Message);
        Assert.Contains("exact_roster_size_required", wrongSize.Message);
        Assert.Contains("duplicate_participation", duplicate.Message);
        Assert.False(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync());
    }

    [Fact]
    public async Task CheckRosterEligibilityAsync_ReturnsPerCandidateReasonCodes()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var outsider = CreateUser("outsider");
        var deleted = CreateUser("deleted");
        deleted.IsDeleted = true;
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member, outsider, deleted);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var eligibility = await service.CheckRosterEligibilityAsync(captain.Auth0UserId, tournament.Id, team.Id, [captain.Id, member.Id, outsider.Id, deleted.Id]);

        Assert.False(eligibility.Eligible);
        Assert.Contains("exact_roster_size_required", eligibility.ReasonCodes);
        Assert.Contains("roster_candidate_ineligible", eligibility.ReasonCodes);
        Assert.Contains(eligibility.Candidates, candidate => candidate.UserId == outsider.Id && candidate.ReasonCodes.Contains("not_team_member"));
        Assert.Contains(eligibility.Candidates, candidate => candidate.UserId == deleted.Id && candidate.ReasonCodes.Contains("user_not_found"));
        Assert.Contains(eligibility.Candidates, candidate => candidate.UserId == deleted.Id && candidate.User is null);
    }

    [Fact]
    public async Task ConfirmRosterAsync_OnlyAllowsSelectedMember()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var other = CreateUser("other");
        var team = CreateTeam(captain, member, other);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member, other);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        var memberRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == member.Id));

        await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmRosterAsync(other.Auth0UserId, tournament.Id, memberRoster.Id));
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(roster => roster.Id == memberRoster.Id && roster.ConfirmationStatus == RosterMemberConfirmationStatus.Pending));
        var active = await service.ConfirmRosterAsync(member.Auth0UserId, tournament.Id, memberRoster.Id);

        Assert.Equal(TournamentRegistrationStatus.Active, active.Status);
    }

    [Fact]
    public async Task ConfirmRosterAsync_RequiresRosterMemberToBelongToTournament()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));
        var memberRoster = Assert.Single(pending.RosterMembers.Where(roster => roster.User.Id == member.Id));

        await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmRosterAsync(member.Auth0UserId, Guid.NewGuid(), memberRoster.Id));

        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(roster =>
            roster.Id == memberRoster.Id && roster.ConfirmationStatus == RosterMemberConfirmationStatus.Pending));
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_SucceedsWhenPostCommitRealtimePublishingFails()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var member = CreateUser("member");
        var team = CreateTeam(captain, member);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext, new ThrowingTournamentRealtimePublisher());

        var result = await service.SubmitTeamRosterAsync(
            captain.Auth0UserId,
            tournament.Id,
            new SubmitTeamRosterDTO(team.Id, [captain.Id, member.Id]));

        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, result.Status);
        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Set<TournamentRegistration>().AnyAsync(registration =>
            registration.TournamentId == tournament.Id &&
            registration.TeamId == team.Id &&
            registration.Status == Mercurius.Modules.Tournament.Domain.TournamentRegistrationStatus.PendingConfirmation));
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(roster =>
            roster.TeamId == team.Id &&
            roster.UserId == member.Id &&
            roster.ConfirmationStatus == RosterMemberConfirmationStatus.Pending));
        Assert.False(await dbContext.Set<TeamInvite>().AnyAsync());
    }

    [Fact]
    public async Task SubmitTeamRosterAsync_ReplacingPendingRosterDeletesOldPendingRoster()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var firstMember = CreateUser("first");
        var secondMember = CreateUser("second");
        var team = CreateTeam(captain, firstMember, secondMember);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, firstMember, secondMember);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var firstRoster = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, firstMember.Id]));
        var firstRosterMemberId = Assert.Single(firstRoster.RosterMembers.Where(roster => roster.User.Id == firstMember.Id)).Id;

        var replacement = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(team.Id, [captain.Id, secondMember.Id]));

        Assert.Equal(TournamentRegistrationStatus.PendingConfirmation, replacement.Status);
        Assert.False(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(member => member.Id == firstRosterMemberId));
        Assert.True(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(member =>
            member.UserId == secondMember.Id &&
            member.TeamId == team.Id &&
            member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending));
        Assert.False(await dbContext.Set<TeamInvite>().AnyAsync());
    }

    [Fact]
    public async Task CaptainUnregisterAndAdminPendingRemoval_DeletePendingRosterMembers()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var firstMember = CreateUser("first");
        var secondMember = CreateUser("second");
        var firstTeam = CreateTeam(captain, firstMember);
        var secondTeam = new Team("Team Beta", captain.Id) { Id = Guid.NewGuid() };
        secondTeam.AddMember(captain.Id);
        secondTeam.AddMember(secondMember.Id);
        var unregisterTournament = CreateTeamTournament(teamSize: 2);
        var adminTournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, firstMember, secondMember);
        dbContext.Teams.AddRange(firstTeam, secondTeam);
        dbContext.Set<TournamentAggregate>().AddRange(unregisterTournament, adminTournament);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        await service.SubmitTeamRosterAsync(captain.Auth0UserId, unregisterTournament.Id, new SubmitTeamRosterDTO(firstTeam.Id, [captain.Id, firstMember.Id]));
        await service.SubmitTeamRosterAsync(captain.Auth0UserId, adminTournament.Id, new SubmitTeamRosterDTO(secondTeam.Id, [captain.Id, secondMember.Id]));

        await service.UnregisterTeamAsync(captain.Auth0UserId, unregisterTournament.Id, firstTeam.Id);
        await service.RemoveTeamAsAdminAsync(adminTournament.Id, secondTeam.Id, "invalid roster", captain.Auth0UserId);

        Assert.False(await dbContext.Set<TournamentRegistration>().AnyAsync(registration => registration.TournamentId == unregisterTournament.Id || registration.TournamentId == adminTournament.Id));
        Assert.False(await dbContext.Set<TournamentRegistrationRosterMember>().AnyAsync(member => member.TournamentId == unregisterTournament.Id || member.TournamentId == adminTournament.Id));
    }

    [Fact]
    public async Task GetAdminRegistrationsAsync_ReturnsPendingAndActiveRosterState()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser("captain");
        var pendingMember = CreateUser("pending");
        var activeCaptain = CreateUser("active-captain");
        var activeMember = CreateUser("active");
        var pendingTeam = CreateTeam(captain, pendingMember);
        var activeTeam = new Team("Team Beta", activeCaptain.Id) { Id = Guid.NewGuid() };
        activeTeam.AddMember(activeCaptain.Id);
        activeTeam.AddMember(activeMember.Id);
        var tournament = CreateTeamTournament(teamSize: 2);
        dbContext.Users.AddRange(captain, pendingMember, activeCaptain, activeMember);
        dbContext.Teams.AddRange(pendingTeam, activeTeam);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, activeTeam, activeCaptain, [activeCaptain, activeMember], DomainTournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var pending = await service.SubmitTeamRosterAsync(captain.Auth0UserId, tournament.Id, new SubmitTeamRosterDTO(pendingTeam.Id, [captain.Id, pendingMember.Id]));

        var registrations = await service.GetAdminRegistrationsAsync(tournament.Id, page: 1, pageSize: 20);

        Assert.Contains(registrations, registration =>
            registration.Id == pending.Id &&
            registration.Status == TournamentRegistrationStatus.PendingConfirmation &&
            registration.RosterMembers.Any(member => member.ConfirmationStatus == TournamentRosterStatus.Pending));
        Assert.Contains(registrations, registration =>
            registration.Status == TournamentRegistrationStatus.Active &&
            registration.RosterMembers.Any(member => member.User.Id == activeMember.Id && member.ConfirmationStatus == TournamentRosterStatus.Confirmed));
    }

    [Fact]
    public async Task GetAdminRegistrationsAsync_PagesBeforeMappingAndHandlesOverflow()
    {
        await using var dbContext = CreateDbContext();
        var first = CreateUser("first");
        var second = CreateUser("second");
        var tournament = CreateIndividualTournament();
        dbContext.Users.AddRange(first, second);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddIndividualRegistration(dbContext, tournament, second);
        AddIndividualRegistration(dbContext, tournament, first);
        await dbContext.SaveChangesAsync();

        var registrations = dbContext.Set<TournamentRegistration>()
            .Where(registration => registration.TournamentId == tournament.Id)
            .OrderBy(registration => registration.Id)
            .ToList();
        registrations[0].CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        registrations[1].CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var firstPage = await service.GetAdminRegistrationsAsync(tournament.Id, page: 1, pageSize: 1);
        var secondPage = await service.GetAdminRegistrationsAsync(tournament.Id, page: 2, pageSize: 1);
        var overflowPage = await service.GetAdminRegistrationsAsync(tournament.Id, page: int.MaxValue, pageSize: 50);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Single(firstPage);
        Assert.Single(secondPage);
        Assert.NotEqual(firstPage[0].Id, secondPage[0].Id);
        Assert.Empty(overflowPage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetAdminRegistrationsAsync(tournament.Id, int.MaxValue, 50, cancellationSource.Token));
    }

    private static TournamentRegistrationService CreateService(
        MercuriusDBContext dbContext,
        ITournamentRealtimePublisher? publisher = null,
        IModuleEventPublisher? moduleEventPublisher = null)
    {
        var identityModule = new IdentityModuleFacade(dbContext, new TournamentTestSupport.NoopAuth0ManagementService());
        var teamsModule = new TeamsModuleFacade(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule,
            new NoopTeamTournamentReadService());

        return new TournamentRegistrationService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            identityModule,
            teamsModule,
            new TournamentEligibilityEvaluator(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)),
            new RegistrationMappingContextBuilder(identityModule, teamsModule),
            new TournamentRegistrationPersistenceCoordinator(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)),
            new TournamentRegistrationReadModelService(
                new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
                teamsModule,
                new RegistrationMappingContextBuilder(identityModule, teamsModule),
                new TournamentDtoMapper(
                    new RegistrationMappingContextBuilder(identityModule, teamsModule),
                    new NullSponsorshipModule())),
            new TournamentDtoMapper(
                new RegistrationMappingContextBuilder(identityModule, teamsModule),
                new NullSponsorshipModule()),
            publisher ?? TournamentTestSupport.CreateRealtimePublisher(),
            moduleEventPublisher ?? TournamentTestSupport.CreateModuleEventPublisher(),
            NullLogger<TournamentRegistrationService>.Instance);
    }

    private static MercuriusDBContext CreateDbContext()
        => PostgresTestDatabase.CreateDbContext();

    private sealed class NoopTeamTournamentReadService : ITeamTournamentReadService
    {
        public Task<IReadOnlyList<PublicTeamTournamentSummary>> GetPublicTeamTournamentsAsync(Guid teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicTeamTournamentSummary>>([]);

        public Task<bool> IsUserInProtectedTournamentRosterAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTeamInDeleteBlockingTournamentAsync(Guid teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> IsTeamLogoReferencedAsync(string logoUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private static TournamentAggregate CreateIndividualTournament()
    {
        return new TournamentAggregate("Solo Cup", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Individual)
        {
            Id = Guid.NewGuid()
        };
    }

    private static TournamentAggregate CreateTeamTournament(int teamSize)
    {
        return new TournamentAggregate("Team Cup", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Team, teamSize)
        {
            Id = Guid.NewGuid()
        };
    }

    private static Team CreateTeam(User captain, params User[] members)
    {
        var team = new Team("Team Alpha", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        foreach (var member in members)
            team.AddMember(member.Id);
        return team;
    }

    private static User CreateUser(string username)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username,
            Firstname = "First",
            Lastname = "Last",
            Email = $"{username}@example.test"
        };
    }

    private static void AddIndividualRegistration(MercuriusDBContext dbContext, TournamentAggregate tournament, User user)
    {
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = DomainTournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username ?? string.Empty,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });
    }

    private static void AddTeamRegistration(
        MercuriusDBContext dbContext,
        TournamentAggregate tournament,
        Team team,
        User captain,
        IReadOnlyCollection<User> rosterMembers,
        Mercurius.Modules.Tournament.Domain.TournamentRegistrationStatus status)
    {
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = status,
            RegisteredByUserId = captain.Id,
            RegisteredByUsernameAtRegistration = captain.Username ?? string.Empty,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId,
            RosterMembers = rosterMembers.Select(member => new TournamentRegistrationRosterMember
            {
                Id = Guid.NewGuid(),
                Tournament = tournament,
                TournamentId = tournament.Id,
                TeamId = team.Id,
                TeamNameAtRegistration = team.Name,
                UserId = member.Id,
                UsernameAtRegistration = member.Username ?? string.Empty,
                DisplayNameAtRegistration = member.DisplayName,
                IsCaptain = member.Id == captain.Id,
                ConfirmationStatus = member.Id == captain.Id
                    ? RosterMemberConfirmationStatus.AutoConfirmed
                    : status == DomainTournamentRegistrationStatus.Active
                        ? RosterMemberConfirmationStatus.Confirmed
                        : RosterMemberConfirmationStatus.Pending,
                ConfirmedAtUtc = member.Id == captain.Id || status == DomainTournamentRegistrationStatus.Active
                    ? DateTime.UtcNow
                    : null
            }).ToList()
        });
    }

    private sealed class NullSponsorshipModule : ISponsorshipModule
    {
        public Task<SponsorSummary?> GetSponsorSummaryAsync(SponsorId sponsorId, CancellationToken cancellationToken = default)
            => Task.FromResult<SponsorSummary?>(null);

        public Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(
            SponsorId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SponsorSearchDocument>>([]);

        public Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(
            IReadOnlyCollection<TournamentId> tournamentIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(new Dictionary<TournamentId, SponsorPlacementSummary>());

        public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(TournamentId tournamentId, CancellationToken cancellationToken = default)
            => Task.FromResult<SponsorPlacementSummary?>(null);

        public Task ReplaceSponsorPlacementAsync(TournamentId tournamentId, SponsorPlacementInput? placement, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingTournamentRealtimePublisher : ITournamentRealtimePublisher
    {
        public Task RosterConfirmationChangedAsync(Guid teamId, Guid notificationId, Guid affectedUserId, string status, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("event publishing failed");
        }
    }
}
