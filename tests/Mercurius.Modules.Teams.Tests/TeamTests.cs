using System.Data.Common;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Teams.DTOs;
using Mercurius.LAN.API.Migrations;
using Mercurius.LAN.API.Hubs;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Teams.Infrastructure;
using Mercurius.Modules.Teams.Services;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Identity.Contracts;
using Platform.Eventing;
using Platform.Realtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mercurius.TestInfrastructure;

namespace Mercurius.Modules.Teams.Tests;

public class TeamTests
{
    private static int _nextId;

    [Fact]
    public void PublicTeamMembersExposeUsernamesWhileManagementSummaryRetainsDetails()
    {
        var publicMemberType = typeof(TeamResponseDTO).GetProperty(nameof(TeamResponseDTO.Members))!
            .PropertyType.GetGenericArguments()[0];
        var managementMemberType = typeof(TeamManagementSummaryResponseDTO).GetProperty(nameof(TeamManagementSummaryResponseDTO.Members))!
            .PropertyType.GetGenericArguments()[0];

        Assert.DoesNotContain(publicMemberType.GetProperties(), property => property.Name is "DiscordId" or "SteamId" or "RiotId");
        Assert.Contains(managementMemberType.GetProperties(), property => property.Name == "DiscordId");
        Assert.Contains(managementMemberType.GetProperties(), property => property.Name == "SteamId");
        Assert.Contains(managementMemberType.GetProperties(), property => property.Name == "RiotId");
    }

    [Fact]
    public void Team_Creation_Should_Set_Properties_Correctly()
    {
        // Arrange
        var teamName = "Test Team";
        var captain = CreateUser();
        var team = CreateTeam(teamName, captain);
        // Act & Assert
        Assert.Equal(teamName, team.Name);
        Assert.Equal("test team", team.NormalizedName);
        Assert.Equal(captain.Id, team.CaptainUserId);
        Assert.Contains(team.Members, member => member.UserId == captain.Id);
    }

    [Fact]
    public void UpdateName_ChangesTeamName()
    {
        var team = CreateTeam();
        var newName = "Updated Team Name";

        team.UpdateName(newName);

        Assert.Equal(newName, team.Name);
        Assert.Equal("updated team name", team.NormalizedName);
    }

    [Fact]
    public async Task CreateTeamAsync_Throws_When_Name_Exists_With_Different_Casing()
    {
        await using var dbContext = CreateDbContext();
        var existingCaptain = CreateUser();
        var newCaptain = CreateUser();

        dbContext.Users.AddRange(existingCaptain, newCaptain);
        dbContext.Teams.Add(CreateTeam("Alpha Squad", existingCaptain));
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => teamService.CreateTeamAsync(new CreateTeamDTO
        {
            Name = "alpha squad",
            CaptainUserId = newCaptain.Id
        }));

        Assert.Contains("already in use", exception.Message);
    }

    [Fact]
    public async Task UpdateTeamAsync_Throws_When_Name_Exists_With_Different_Casing()
    {
        await using var dbContext = CreateDbContext();
        var firstCaptain = CreateUser();
        var secondCaptain = CreateUser();
        var firstTeam = CreateTeam("Alpha Squad", firstCaptain);
        var secondTeam = CreateTeam("Beta Squad", secondCaptain);

        dbContext.Users.AddRange(firstCaptain, secondCaptain);
        dbContext.Teams.AddRange(firstTeam, secondTeam);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => teamService.UpdateTeamAsync(secondTeam.Id, new UpdateTeamDTO
        {
            Name = "ALPHA SQUAD"
        }));

        Assert.Contains("already in use", exception.Message);
    }

    [Fact]
    public async Task CreateTeamAsync_ThrowsValidation_When_DatabaseUniqueConstraintFails()
    {
        await using var dbContext = CreateUniqueConstraintDbContext();
        var captain = CreateUser();

        dbContext.Users.Add(captain);
        await dbContext.SaveChangesAsync();
        dbContext.ThrowTeamNameUniqueConstraint = true;

        var teamService = CreateTeamService(dbContext);

        var exception = await Assert.ThrowsAsync<ValidationException>(() => teamService.CreateTeamAsync(new CreateTeamDTO
        {
            Name = "Alpha Squad",
            CaptainUserId = captain.Id
        }));

        Assert.Contains("already in use", exception.Message);
    }

    [Fact]
    public async Task GetTeamByNameAsync_ReturnsTeamDto_When_CasingDiffers()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha Squad", captain);

        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        var result = await teamService.GetTeamByNameAsync("ALPHA SQUAD");

        Assert.Equal(team.Id, result.Id);
    }

    [Fact]
    public async Task SearchTeamsByNameAsync_ReturnsMatches_When_QueryCasingDiffers()
    {
        await using var dbContext = CreateDbContext();
        var alphaCaptain = CreateUser();
        var alpineCaptain = CreateUser();
        var betaCaptain = CreateUser();

        dbContext.Users.AddRange(alphaCaptain, alpineCaptain, betaCaptain);
        dbContext.Teams.AddRange(
            CreateTeam("Alpha Squad", alphaCaptain),
            CreateTeam("Alpine Club", alpineCaptain),
            CreateTeam("Beta Squad", betaCaptain));
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        var results = (await teamService.SearchTeamsByNameAsync("ALP")).ToList();

        Assert.Contains(results, team => team.Name == "Alpha Squad");
        Assert.Contains(results, team => team.Name == "Alpine Club");
        Assert.DoesNotContain(results, team => team.Name == "Beta Squad");
    }

    [Fact]
    public async Task GetAllTeamsAsync_ReturnsDeterministicNavigablePages_WithOneIdentityBatchPerPage()
    {
        await using var dbContext = CreateDbContext();
        var alphaCaptain = CreateUser();
        var betaCaptain = CreateUser();
        var deltaCaptain = CreateUser();
        var gammaCaptain = CreateUser();
        dbContext.Users.AddRange(alphaCaptain, betaCaptain, deltaCaptain, gammaCaptain);
        dbContext.Teams.AddRange(
            CreateTeam("Gamma", gammaCaptain),
            CreateTeam("Alpha", alphaCaptain),
            CreateTeam("Delta", deltaCaptain),
            CreateTeam("Beta", betaCaptain));
        await dbContext.SaveChangesAsync();
        var identityModule = new DbContextIdentityModule(dbContext);
        var service = CreateTeamQueryService(dbContext, identityModule);

        var firstPage = await service.GetAllTeamsAsync(1, 2);
        var secondPage = await service.GetAllTeamsAsync(2, 2);

        Assert.Equal(["Alpha", "Beta"], firstPage.Select(team => team.Name).ToArray());
        Assert.Equal(["Delta", "Gamma"], secondPage.Select(team => team.Name).ToArray());
        Assert.Equal(2, identityModule.BatchCallCount);
        Assert.Equal(
            secondPage.SelectMany(team => team.Members).Select(member => new UserId(member.Id)).OrderBy(id => id.Value),
            identityModule.LastBatchUserIds.OrderBy(id => id.Value));
    }

    [Fact]
    public async Task GetAllTeamsAsync_OverflowingOffset_ReturnsEmptyWithoutIdentityLookup()
    {
        await using var dbContext = CreateDbContext();
        var identityModule = new DbContextIdentityModule(dbContext);
        var service = CreateTeamQueryService(dbContext, identityModule);

        var page = await service.GetAllTeamsAsync(int.MaxValue, 50);

        Assert.Empty(page);
        Assert.Equal(0, identityModule.BatchCallCount);
    }

    [Fact]
    public async Task GetAllTeamsAsync_OverflowingOffset_ObservesCancellation()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateTeamQueryService(dbContext, new DbContextIdentityModule(dbContext));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetAllTeamsAsync(int.MaxValue, 50, cancellation.Token));
    }

    [Fact]
    public void NormalizeName_ThrowsValidation_When_NameIsInvalid()
    {
        Assert.Throws<ValidationException>(() => Team.NormalizeName("   "));
        Assert.Throws<ValidationException>(() => Team.NormalizeName(new string('a', 101)));
        Assert.Throws<ValidationException>(() => Team.NormalizeName("Alpha\nSquad"));
    }

    [Fact]
    public void TeamNameNormalizationMigration_BackfillsNormalizedNamesBeforeUniqueIndex()
    {
        var migration = new TeamNameNormalization();
        var operations = migration.UpOperations.ToList();

        var addColumnIndex = operations.FindIndex(operation =>
            operation is AddColumnOperation addColumn &&
            addColumn.Table == "Teams" &&
            addColumn.Name == "NormalizedName" &&
            addColumn.IsNullable);
        var backfillIndex = operations.FindIndex(operation =>
            operation is SqlOperation sqlOperation &&
            sqlOperation.Sql.Contains("lower(btrim(\"Name\"))", StringComparison.Ordinal));
        var alterColumnIndex = operations.FindIndex(operation =>
            operation is AlterColumnOperation alterColumn &&
            alterColumn.Table == "Teams" &&
            alterColumn.Name == "NormalizedName" &&
            !alterColumn.IsNullable);
        var uniqueIndexIndex = operations.FindIndex(operation =>
            operation is CreateIndexOperation createIndex &&
            createIndex.Table == "Teams" &&
            createIndex.Name == "IX_Teams_NormalizedName" &&
            createIndex.IsUnique);

        Assert.True(addColumnIndex >= 0);
        Assert.True(addColumnIndex < backfillIndex);
        Assert.True(backfillIndex < alterColumnIndex);
        Assert.True(alterColumnIndex < uniqueIndexIndex);
    }

    [Fact]
    public void UserOwnedTeamManagementMigration_AddsLogoInviteStateIndexesAndBackfills()
    {
        var migration = new UserOwnedTeamManagement();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations, operation =>
            operation is AddColumnOperation addColumn &&
            addColumn.Table == "Teams" &&
            addColumn.Name == "LogoUrl");
        Assert.Contains(operations, operation =>
            operation is AddColumnOperation addColumn &&
            addColumn.Table == "TeamInvites" &&
            addColumn.Name == "ExpiresAt" &&
            addColumn.IsNullable);
        Assert.Contains(operations, operation =>
            operation is CreateIndexOperation createIndex &&
            createIndex.Table == "TeamInvites" &&
            createIndex.Name == "IX_TeamInvites_TeamId_UserId_Pending" &&
            createIndex.IsUnique);
        Assert.Contains(operations, operation =>
            operation is SqlOperation sqlOperation &&
            sqlOperation.Sql.Contains("WHERE \"ExpiresAt\" IS NULL", StringComparison.Ordinal));
        Assert.Contains(operations, operation =>
            operation is AlterColumnOperation alterColumn &&
            alterColumn.Table == "TeamInvites" &&
            alterColumn.Name == "ExpiresAt" &&
            !alterColumn.IsNullable);
        Assert.Contains(operations, operation =>
            operation is SqlOperation sqlOperation &&
            sqlOperation.Sql.Contains("INSERT INTO \"TeamUser\"", StringComparison.Ordinal));
    }



    [Fact]
    public void ChangeCaptain_ChangesCaptain_WhenUserIsTeamMember()
    {
        var team = CreateTeam();
        var newCaptain = CreateUser();
        team.AddMember(newCaptain.Id);

        team.ChangeCaptain(newCaptain.Id);

        Assert.Equal(newCaptain.Id, team.CaptainUserId);
    }

    [Fact]
    public void ChangeCaptain_Throws_WhenUserIsNotTeamMember()
    {
        var team = CreateTeam();
        var outsider = CreateUser();

        Assert.Throws<ValidationException>(() => team.ChangeCaptain(outsider.Id));
    }

    [Fact]
    public void RemoveMember_Should_Remove_User_From_Team()
    {
        // Arrange
        var team = CreateTeam();
        var memberToRemove = CreateUser();
        team.AddMember(memberToRemove.Id);
        // Act
        team.RemoveMember(memberToRemove.Id);
        // Assert
        Assert.DoesNotContain(team.Members, member => member.UserId == memberToRemove.Id);
    }
    [Fact]
    public void RemoveMember_Should_Not_Remove_User_If_Not_In_Team()
    {
        // Arrange
        var team = CreateTeam();
        var memberToRemove = CreateUser();
        // Act & Assert
        Assert.Throws<NotFoundException>(() => team.RemoveMember(memberToRemove.Id));
    }

    [Fact]
    public void RemoveMember_Should_Not_Remove_Captain_From_Team()
    {
        // Arrange
        var team = CreateTeam();
        var captainUserId = team.CaptainUserId!.Value;
        // Act & Assert
        Assert.Throws<ValidationException>(() => team.RemoveMember(captainUserId));
    }

    [Fact]
    public void InviteUser_Should_Throw_When_User_Already_In_Team()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.AddMember(userToInvite.Id);
        // Act & Assert
        Assert.Throws<ValidationException>(() => team.InviteUser(userToInvite.Id, 7));
    }

    [Fact]
    public void InviteUser_Should_Throw_When_Pending_Invite_Exists()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Add(new TeamInvite { UserId = userToInvite.Id, TeamId = team.Id, Status = TeamInviteStatus.Pending });
        // Act & Assert
        Assert.Throws<ValidationException>(() => team.InviteUser(userToInvite.Id, 7));
    }

    [Fact]
    public void InviteUser_Should_Add_Invite_When_Valid()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear(); // Ensure no existing invites
        // Act
        team.InviteUser(userToInvite.Id, 7);
        // Assert
        Assert.Single(team.TeamInvites);
        Assert.Equal(userToInvite.Id, team.TeamInvites.First().UserId);
        Assert.Equal(team.Id, team.TeamInvites.First().TeamId);
        Assert.Equal(TeamInviteStatus.Pending, team.TeamInvites.First().Status);
    }

    [Fact]
    public void InviteUser_Should_Allow_Resend_When_Declined_Limit_Not_Reached()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear(); // Ensure no existing invites
        team.TeamInvites.Add(new TeamInvite
        {
            UserId = userToInvite.Id,
            TeamId = team.Id,
            Status = TeamInviteStatus.Declined,
            RespondedAt = DateTime.UtcNow.AddDays(-5) // Declined 5 days ago
        });

        var invite = team.InviteUser(userToInvite.Id, 7);

        Assert.Equal(TeamInviteStatus.Pending, invite.Status);
    }

    [Fact]
    public void InviteUser_Should_Throw_When_Declined_Limit_Reached_Too_Soon()
    {
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear();
        foreach (var index in Enumerable.Range(0, 3))
        {
            team.TeamInvites.Add(new TeamInvite
            {
                UserId = userToInvite.Id,
                TeamId = team.Id,
                Status = TeamInviteStatus.Declined,
                RespondedAt = DateTime.UtcNow.AddDays(-index - 1)
            });
        }

        Assert.Throws<ValidationException>(() => team.InviteUser(userToInvite.Id, 7));
    }

    [Fact]
    public void TeamInvite_Respond_Accept_Should_Update_Invite_Accepted()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear(); // Ensure no existing invites
        var invite = team.InviteUser(userToInvite.Id, 7);

        //Have to do this manually because Actual references are handled by EF Core
        invite.Team = team;

        // Act
        invite.Respond(true);

        // Assert
        Assert.Equal(TeamInviteStatus.Accepted, invite.Status);
        Assert.Contains(team.Members, member => member.UserId == userToInvite.Id);
        Assert.NotNull(invite.RespondedAt);
    }

    [Fact]
    public void TeamInvite_Respond_Decline_Should_Update_Invite_Declined()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear(); // Ensure no existing invites
        var invite = team.InviteUser(userToInvite.Id, 7);
        // Act
        invite.Respond(false);
        // Assert
        Assert.Equal(TeamInviteStatus.Declined, invite.Status);
        Assert.DoesNotContain(team.Members, member => member.UserId == userToInvite.Id);
        Assert.NotNull(invite.RespondedAt);
    }

    [Fact]
    public void TeamInvite_Respond_Should_Throw_When_Invite_Is_Not_Pending()
    {
        // Arrange
        var team = CreateTeam();
        var userToInvite = CreateUser();
        team.TeamInvites.Clear(); // Ensure no existing invites
        var invite = team.InviteUser(userToInvite.Id, 7);
        invite.Team = team;
        invite.Status = TeamInviteStatus.Accepted; // Change status to Accepted
        // Act & Assert
        Assert.Throws<ValidationException>(() => invite.Respond(true));
    }

    [Fact]
    public void TeamInvite_Respond_Should_Expire_When_Invite_Is_PastExpiration()
    {
        var team = CreateTeam();
        var userToInvite = CreateUser();
        var invite = team.InviteUser(userToInvite.Id, 7);
        invite.Team = team;
        invite.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);

        Assert.Throws<ValidationException>(() => invite.Respond(true));
        Assert.Equal(TeamInviteStatus.Expired, invite.Status);
        Assert.NotNull(invite.ExpiredAt);
    }

    [Fact]
    public async Task CreateCurrentUserTeamAsync_UsesCurrentUserAndEnforcesCaptainLimit()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        dbContext.Users.Add(captain);
        dbContext.Teams.AddRange(
            CreateTeam("One", captain),
            CreateTeam("Two", captain));
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.CreateCurrentUserTeamAsync(captain.Auth0UserId, new CreateTeamDTO { Name = "Three" }));
    }

    [Fact]
    public async Task InviteUserAsync_RequiresCurrentCaptain()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var outsider = CreateUser();
        var invited = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, outsider, invited);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            teamService.InviteUserAsync(outsider.Auth0UserId, team.Id, invited.Id));
    }

    [Fact]
    public async Task CancelInviteAsync_MarksPendingInviteCancelled()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var invited = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, invited);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);
        var invite = await teamService.InviteUserAsync(captain.Auth0UserId, team.Id, invited.Id);

        var result = await teamService.CancelInviteAsync(captain.Auth0UserId, team.Id, invite.Id);

        Assert.Equal(nameof(TeamInviteStatus.Cancelled), result.Status);
        Assert.NotNull(result.CancelledAt);
    }

    [Fact]
    public async Task RespondToInviteAsync_OnlyAllowsRecipient()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var invited = CreateUser();
        var otherUser = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, invited, otherUser);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);
        var invite = await teamService.InviteUserAsync(captain.Auth0UserId, team.Id, invited.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            teamService.RespondToInviteAsync(otherUser.Auth0UserId, invite.Id, true));
    }

    [Theory]
    [InlineData((int)TournamentStatus.InProgress)]
    public async Task LeaveTeamAsync_BlocksProtectedTournamentStatuses(int status)
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        var tournament = new TournamentAggregate("Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid(),
            Status = (TournamentStatus)status
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, team, captain, [captain, member], TournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.LeaveTeamAsync(member.Auth0UserId, team.Id));
    }

    [Fact]
    public async Task RemoveMemberAsync_AllowsCaptainToRemoveNonCaptainMember()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var publisher = new RecordingTeamEventPublisher();
        var teamService = CreateTeamService(dbContext, eventPublisher: publisher);

        var result = await teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, member.Id);

        Assert.DoesNotContain(result.Members, teamMember => teamMember.Id == member.Id);
        Assert.DoesNotContain(team.Members, teamMember => teamMember.UserId == member.Id);
        Assert.Contains(publisher.MembershipEvents, evt => evt.TeamId == team.Id && evt.UserId == member.Id && evt.Action == "Removed");
    }

    [Fact]
    public async Task RemoveMemberAsync_RequiresCurrentCaptain()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var outsider = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member, outsider);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            teamService.RemoveMemberAsync(outsider.Auth0UserId, team.Id, member.Id));

        Assert.Contains(team.Members, teamMember => teamMember.UserId == member.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_RejectsCaptainRemoval()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, captain.Id));

        Assert.Contains(team.Members, teamMember => teamMember.UserId == captain.Id);
    }

    [Fact]
    public async Task RemoveMemberAsync_BlocksInProgressTournamentRoster()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        var tournament = new TournamentAggregate("Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid(),
            Status = TournamentStatus.InProgress
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, team, captain, [captain, member], TournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, member.Id));

        Assert.Contains(team.Members, teamMember => teamMember.UserId == member.Id);
    }

    [Theory]
    [InlineData((int)TournamentStatus.Completed)]
    [InlineData((int)TournamentStatus.Canceled)]
    public async Task RemoveMemberAsync_AllowsCompletedAndCanceledTournamentRosters(int status)
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        var tournament = new TournamentAggregate("Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid(),
            Status = (TournamentStatus)status
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, team, captain, [captain, member], TournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, member.Id);

        Assert.DoesNotContain(team.Members, teamMember => teamMember.UserId == member.Id);
    }

    [Fact]
    public async Task DeleteTeamAsync_RequiresCurrentCaptain()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var outsider = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, outsider);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            teamService.DeleteTeamAsync(outsider.Auth0UserId, team.Id));

        Assert.False((await dbContext.Teams.FindAsync(team.Id))!.IsDeleted);
    }

    [Theory]
    [InlineData((int)TournamentStatus.Scheduled)]
    [InlineData((int)TournamentStatus.InProgress)]
    public async Task DeleteTeamAsync_BlocksActiveTournamentStatuses(int status)
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        var tournament = new TournamentAggregate("Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 1)
        {
            Id = Guid.NewGuid(),
            Status = (TournamentStatus)status
        };
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, team, captain, [captain], TournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id));

        Assert.False((await dbContext.Teams.FindAsync(team.Id))!.IsDeleted);
    }

    [Fact]
    public async Task DeleteTeamAsync_SoftDeletesAndPreservesHistoricalReferences()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/alpha.webp";
        team.AddMember(member.Id);
        var tournament = new TournamentAggregate("Completed Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid(),
            Status = TournamentStatus.Completed
        };
        var placement = new Placement { Id = Guid.NewGuid(), Tournament = tournament, TournamentId = tournament.Id, Place = 1 };
        var match = new Match
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            ParticipationMode = ParticipationMode.Team,
            TeamParticipant1Id = team.Id
        };
        AddTeamRegistration(dbContext, tournament, team, captain, [captain, member], TournamentRegistrationStatus.Active);
        placement.Teams = [new PlacementTeam { TeamId = team.Id }];
        var invite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = member.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(invite);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<Placement>().Add(placement);
        dbContext.Set<Match>().Add(match);
        await dbContext.SaveChangesAsync();

        var mediaModule = new StubMediaModule("/images/replacement.webp");
        var teamService = CreateTeamService(dbContext, mediaModule);

        await teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id);

        var deletedTeam = await dbContext.Teams.FindAsync(team.Id);
        Assert.NotNull(deletedTeam);
        Assert.True(deletedTeam.IsDeleted);
        Assert.NotNull(deletedTeam.DeletedAtUtc);
        Assert.StartsWith("deleted-team-", deletedTeam.Name);
        Assert.Equal(deletedTeam.Name, deletedTeam.NormalizedName);
        Assert.Null(deletedTeam.CaptainUserId);
        Assert.Null(deletedTeam.LogoUrl);
        Assert.Empty(team.Members);
        Assert.False(await dbContext.Set<TeamInvite>().AnyAsync(teamInvite => teamInvite.TeamId == team.Id));
        Assert.True(await dbContext.Set<TournamentRegistration>().AnyAsync(registration => registration.TournamentId == tournament.Id && registration.TeamId == team.Id));
        Assert.True(await dbContext.Set<Match>().AnyAsync(m => m.Id == match.Id && m.TeamParticipant1Id == team.Id));
        Assert.True(await dbContext.Set<Placement>().AnyAsync(p => p.Id == placement.Id && p.Teams.Any(t => t.TeamId == team.Id)));
        Assert.Empty(mediaModule.DeletedImages);
    }

    [Fact]
    public async Task DeleteTeamAsync_AllowsDeletedTeamNameToBeReused()
    {
        await using var dbContext = CreateDbContext();
        var originalCaptain = CreateUser();
        var newCaptain = CreateUser();
        var team = CreateTeam("Alpha", originalCaptain);
        dbContext.Users.AddRange(originalCaptain, newCaptain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var teamService = CreateTeamService(dbContext);

        await teamService.DeleteTeamAsync(originalCaptain.Auth0UserId, team.Id);
        var created = await teamService.CreateCurrentUserTeamAsync(newCaptain.Auth0UserId, new CreateTeamDTO { Name = "Alpha" });

        Assert.Equal("Alpha", created.Name);
        Assert.NotEqual(team.Id, created.Id);
    }

    [Fact]
    public async Task DeletedTeams_AreHiddenFromActiveTeamSurfaces()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id);

        Assert.Empty(await teamService.GetAllTeamsAsync(1, 20));
        Assert.Empty(await teamService.SearchTeamsByNameAsync("alp"));
        await Assert.ThrowsAsync<NotFoundException>(() => teamService.GetTeamByIdAsync(team.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => teamService.GetPublicTeamProfileAsync("alpha"));
        var summary = await teamService.GetCurrentUserTeamSummaryAsync(captain.Auth0UserId);
        Assert.Empty(summary.CaptainedTeams);
        Assert.Empty(summary.MemberTeams);
    }

    [Fact]
    public async Task TransferCaptainAsync_RejectsRecipientCaptainLimit()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var target = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(target.Id);
        dbContext.Users.AddRange(captain, target);
        dbContext.Teams.AddRange(
            team,
            CreateTeam("Target One", target),
            CreateTeam("Target Two", target));
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.TransferCaptainAsync(captain.Auth0UserId, team.Id, target.Id));
    }

    [Fact]
    public async Task UploadTeamLogoAsync_StoresSafeReferenceAndPublicProfileReturnsIt()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext, new StubMediaModule("/images/team-logo.webp"));

        var result = await teamService.UploadTeamLogoAsync(captain.Auth0UserId, team.Id, CreateFormFile());
        var profile = await teamService.GetPublicTeamProfileAsync("alpha");

        Assert.Equal("/images/team-logo.webp", result.LogoUrl);
        Assert.Equal("/images/team-logo.webp", profile.LogoUrl);
    }

    [Fact]
    public async Task UploadTeamLogoAsync_CancellationAfterSaveCompensatesNewLogoWithNonCancelledToken()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        using var cancellation = new CancellationTokenSource();
        var mediaModule = new StubMediaModule("/images/new.webp")
        {
            CancelAfterSave = cancellation
        };
        var teamService = CreateTeamService(dbContext, mediaModule);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teamService.UploadTeamLogoAsync(
                captain.Auth0UserId,
                team.Id,
                CreateFormFile(),
                cancellation.Token));

        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal("/images/new.webp", deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            "/images/original.webp",
            (await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).LogoUrl);
    }

    [Fact]
    public async Task UploadTeamLogoAsync_SuccessRetiresOnlyPreviousLogo()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var mediaModule = new StubMediaModule("/images/new.webp");
        var teamService = CreateTeamService(dbContext, mediaModule);

        var result = await teamService.UploadTeamLogoAsync(captain.Auth0UserId, team.Id, CreateFormFile());

        Assert.Equal("/images/new.webp", result.LogoUrl);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal("/images/original.webp", deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
    }

    [Fact]
    public async Task UploadTeamLogoAsync_DoesNotRetireUnchangedOrHistoricallyReferencedLogo()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        var tournament = new TournamentAggregate("Completed Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 1)
        {
            Id = Guid.NewGuid(),
            Status = TournamentStatus.Completed
        };
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        AddTeamRegistration(dbContext, tournament, team, captain, [captain], TournamentRegistrationStatus.Active);
        await dbContext.SaveChangesAsync();
        var historicalMediaModule = new StubMediaModule("/images/new.webp");
        var teamService = CreateTeamService(dbContext, historicalMediaModule);

        await teamService.UploadTeamLogoAsync(captain.Auth0UserId, team.Id, CreateFormFile());

        Assert.Empty(historicalMediaModule.DeletedImages);

        var unchangedMediaModule = new StubMediaModule("/images/new.webp");
        teamService = CreateTeamService(dbContext, unchangedMediaModule);
        await teamService.UploadTeamLogoAsync(captain.Auth0UserId, team.Id, CreateFormFile());
        Assert.Empty(unchangedMediaModule.DeletedImages);
    }

    [Fact]
    public async Task RemoveTeamLogoAsync_DoesNotRetireLogoSharedByAnotherCurrentTeam()
    {
        await using var dbContext = CreateDbContext();
        var firstCaptain = CreateUser();
        var secondCaptain = CreateUser();
        var firstTeam = CreateTeam("Alpha", firstCaptain);
        var secondTeam = CreateTeam("Beta", secondCaptain);
        firstTeam.LogoUrl = "/images/shared.webp";
        secondTeam.LogoUrl = "/images/shared.webp";
        dbContext.Users.AddRange(firstCaptain, secondCaptain);
        dbContext.Teams.AddRange(firstTeam, secondTeam);
        await dbContext.SaveChangesAsync();
        var mediaModule = new StubMediaModule("/images/unused.webp");
        var teamService = CreateTeamService(dbContext, mediaModule);

        await teamService.RemoveTeamLogoAsync(firstCaptain.Auth0UserId, firstTeam.Id);

        Assert.Empty(mediaModule.DeletedImages);
    }

    [Fact]
    public async Task GetCurrentUserTeamSummaryAsync_ReturnsCaptainedMemberAndInviteViews()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var otherCaptain = CreateUser();
        var captainedTeam = CreateTeam("Captained", captain);
        var memberTeam = CreateTeam("Member", otherCaptain);
        memberTeam.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member, otherCaptain);
        dbContext.Teams.AddRange(captainedTeam, memberTeam);
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);
        await teamService.InviteUserAsync(captain.Auth0UserId, captainedTeam.Id, member.Id);

        var summary = await teamService.GetCurrentUserTeamSummaryAsync(member.Auth0UserId);

        Assert.Contains(summary.MemberTeams, team => team.Id == memberTeam.Id);
        Assert.Contains(summary.ReceivedPendingInvites, invite => invite.TeamId == captainedTeam.Id);
    }

    [Fact]
    public async Task GetCurrentUserTeamSummaryAsync_DoesNotReturnRosterConfirmationsAsTeamInvites()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Tournament Team", captain);
        team.AddMember(member.Id);
        var tournament = new TournamentAggregate("Team Cup", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid()
        };
        var rosterMember = new TournamentRegistrationRosterMember
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            UserId = member.Id,
            UsernameAtRegistration = member.Username ?? string.Empty,
            DisplayNameAtRegistration = member.DisplayName,
            ConfirmationStatus = RosterMemberConfirmationStatus.Pending
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<TournamentRegistration>().Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.PendingConfirmation,
            RegisteredByUserId = captain.Id,
            RegisteredByUsernameAtRegistration = captain.Username ?? string.Empty,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId,
            RosterMembers = [rosterMember]
        });

        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext);

        var summary = await teamService.GetCurrentUserTeamSummaryAsync(member.Auth0UserId);

        Assert.Empty(summary.ReceivedPendingInvites);
        Assert.Empty(await teamService.GetCurrentUserInvitesAsync(member.Auth0UserId));
    }

    [Fact]
    public async Task CurrentUserInviteReads_DoNotMaintainRelatedOrUnrelatedInvites()
    {
        await using var dbContext = CreateDbContext();
        var currentUser = CreateUser();
        var receivedCaptain = CreateUser();
        var sentRecipient = CreateUser();
        var unrelatedCaptain = CreateUser();
        var unrelatedRecipient = CreateUser();
        var currentUserTeam = CreateTeam("Current user team", currentUser);
        var receivedTeam = CreateTeam("Received team", receivedCaptain);
        var unrelatedTeam = CreateTeam("Unrelated team", unrelatedCaptain);
        var now = DateTime.UtcNow;
        var receivedDueInvite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = receivedTeam,
            TeamId = receivedTeam.Id,
            UserId = currentUser.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = now.AddDays(-20),
            ExpiresAt = now.AddDays(-1)
        };
        var sentDueInvite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = currentUserTeam,
            TeamId = currentUserTeam.Id,
            UserId = sentRecipient.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = now.AddDays(-20),
            ExpiresAt = now.AddDays(-1)
        };
        var unrelatedDueInvite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = unrelatedTeam,
            TeamId = unrelatedTeam.Id,
            UserId = unrelatedRecipient.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = now.AddDays(-20),
            ExpiresAt = now.AddDays(-1)
        };
        var oldTerminalInvite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = currentUserTeam,
            TeamId = currentUserTeam.Id,
            UserId = sentRecipient.Id,
            Status = TeamInviteStatus.Declined,
            CreatedAt = now.AddDays(-120),
            ExpiresAt = now.AddDays(-100),
            RespondedAt = now.AddDays(-100)
        };
        dbContext.Users.AddRange(currentUser, receivedCaptain, sentRecipient, unrelatedCaptain, unrelatedRecipient);
        dbContext.Teams.AddRange(currentUserTeam, receivedTeam, unrelatedTeam);
        dbContext.Set<TeamInvite>().AddRange(receivedDueInvite, sentDueInvite, unrelatedDueInvite, oldTerminalInvite);
        await dbContext.SaveChangesAsync();

        var publisher = new RecordingTeamEventPublisher();
        var teamService = CreateTeamService(dbContext, eventPublisher: publisher);

        var summary = await teamService.GetCurrentUserTeamSummaryAsync(currentUser.Auth0UserId);
        var receivedInvites = await teamService.GetCurrentUserInvitesAsync(currentUser.Auth0UserId);
        var sentInvites = await teamService.GetCurrentUserSentInvitesAsync(currentUser.Auth0UserId);

        Assert.Empty(summary.ReceivedPendingInvites);
        Assert.Empty(summary.SentPendingInvites);
        Assert.Empty(receivedInvites);
        Assert.Empty(sentInvites);
        Assert.Empty(publisher.InviteEvents);

        dbContext.ChangeTracker.Clear();
        var persistedInvites = await dbContext.Set<TeamInvite>()
            .ToDictionaryAsync(invite => invite.Id);

        Assert.Equal(4, persistedInvites.Count);
        Assert.Equal(TeamInviteStatus.Pending, persistedInvites[receivedDueInvite.Id].Status);
        Assert.Equal(TeamInviteStatus.Pending, persistedInvites[sentDueInvite.Id].Status);
        Assert.Equal(TeamInviteStatus.Pending, persistedInvites[unrelatedDueInvite.Id].Status);
        Assert.Equal(TeamInviteStatus.Declined, persistedInvites[oldTerminalInvite.Id].Status);
    }

    [Fact]
    public async Task TeamManagementEvents_ArePublishedForInviteMembershipAndCaptainTransfer()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var invited = CreateUser();
        var team = CreateTeam("Alpha", captain);
        dbContext.Users.AddRange(captain, invited);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        var publisher = new RecordingTeamEventPublisher();
        var teamService = CreateTeamService(dbContext, eventPublisher: publisher);

        var invite = await teamService.InviteUserAsync(captain.Auth0UserId, team.Id, invited.Id);
        await teamService.RespondToInviteAsync(invited.Auth0UserId, invite.Id, true);
        await teamService.TransferCaptainAsync(captain.Auth0UserId, team.Id, invited.Id);

        Assert.Contains(publisher.InviteEvents, evt => evt.TeamId == team.Id && evt.InviteId == invite.Id && evt.Status == nameof(TeamInviteStatus.Pending));
        Assert.Contains(publisher.InviteEvents, evt => evt.TeamId == team.Id && evt.InviteId == invite.Id && evt.Status == nameof(TeamInviteStatus.Accepted));
        Assert.Contains(publisher.MembershipEvents, evt => evt.TeamId == team.Id && evt.UserId == invited.Id && evt.Action == "Joined");
        Assert.Contains(publisher.CaptainEvents, evt => evt.TeamId == team.Id && evt.NewCaptainUserId == invited.Id);
    }

    [Fact]
    public async Task InviteUserAsync_PreservesOriginalException_WhenExpiredInvitePublishFails()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = member.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });
        await dbContext.SaveChangesAsync();

        var teamService = CreateTeamService(dbContext, eventPublisher: new ThrowingInviteChangedTeamEventPublisher());

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            teamService.InviteUserAsync(captain.Auth0UserId, team.Id, member.Id));

        Assert.Equal("User is already in the team", exception.Message);
    }

    [Fact]
    public async Task RemoveMemberAsync_RevokesAfterCommitBeforeMembershipBroadcast()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var operationOrder = new List<string>();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OperationOrder = operationOrder
        };
        var publisher = new RecordingTeamEventPublisher(operationOrder);
        var teamService = CreateTeamService(
            dbContext,
            eventPublisher: publisher,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        await teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, member.Id);

        Assert.Equal(["RevokeUserFromGroup", "MembershipBroadcast"], operationOrder);
        var revocation = Assert.Single(realtimeConnectionManager.UserGroupRevocations);
        Assert.Equal(member.Id, revocation.UserId);
        Assert.Equal(TeamRealtimeGroups.GetTeamGroup(team.Id), revocation.GroupName);
        Assert.Equal(CancellationToken.None, revocation.CancellationToken);
    }

    [Fact]
    public async Task LeaveAndDeleteTeamAsync_RevokeAffectedProcessLocalGroups()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var leavingTeam = CreateTeam("Leaving", captain);
        var deletedTeam = CreateTeam("Deleted", captain);
        leavingTeam.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.AddRange(leavingTeam, deletedTeam);
        await dbContext.SaveChangesAsync();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager();
        var teamService = CreateTeamService(
            dbContext,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        await teamService.LeaveTeamAsync(member.Auth0UserId, leavingTeam.Id);
        await teamService.DeleteTeamAsync(captain.Auth0UserId, deletedTeam.Id);

        Assert.Contains(
            realtimeConnectionManager.UserGroupRevocations,
            revocation =>
                revocation.UserId == member.Id &&
                revocation.GroupName == TeamRealtimeGroups.GetTeamGroup(leavingTeam.Id) &&
                revocation.CancellationToken == CancellationToken.None);
        Assert.Contains(
            realtimeConnectionManager.GroupRevocations,
            revocation =>
                revocation.GroupName == TeamRealtimeGroups.GetTeamGroup(deletedTeam.Id) &&
                revocation.CancellationToken == CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteTeamAsync_CancellationAfterCommitStillNotifiesRemovedUsers(bool deleteByTeamId)
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var invitee = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        var invite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = invitee.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        dbContext.Users.AddRange(captain, member, invitee);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().Add(invite);
        await dbContext.SaveChangesAsync();

        using var requestCancellation = new CancellationTokenSource();
        var publisher = new RecordingTeamEventPublisher();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OnRevocation = requestCancellation.Cancel
        };
        var teamService = CreateTeamService(
            dbContext,
            eventPublisher: publisher,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        if (deleteByTeamId)
            await teamService.DeleteTeamAsync(team.Id, requestCancellation.Token);
        else
            await teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id, requestCancellation.Token);

        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.Contains(publisher.MembershipEvents, evt => evt.TeamId == team.Id && evt.UserId == member.Id && evt.Action == "Removed");
        Assert.Contains(publisher.InviteEvents, evt => evt.TeamId == team.Id && evt.InviteId == invite.Id && evt.UserId == invitee.Id && evt.Status == nameof(TeamInviteStatus.Cancelled));
        Assert.All(publisher.MembershipCancellationTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.All(publisher.InviteCancellationTokens, token => Assert.Equal(CancellationToken.None, token));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("leave")]
    [InlineData("accept")]
    public async Task MembershipNotificationsAfterCommitIgnoreRequestCancellation(string operation)
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var affectedUser = CreateUser();
        var team = CreateTeam("Alpha", captain);
        TeamInvite? invite = null;
        if (operation is "remove" or "leave")
            team.AddMember(affectedUser.Id);
        else
        {
            invite = new TeamInvite
            {
                Id = Guid.NewGuid(),
                Team = team,
                TeamId = team.Id,
                UserId = affectedUser.Id,
                Status = TeamInviteStatus.Pending,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            };
            dbContext.Set<TeamInvite>().Add(invite);
        }

        dbContext.Users.AddRange(captain, affectedUser);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();

        using var requestCancellation = new CancellationTokenSource();
        var publisher = new RecordingTeamEventPublisher
        {
            HonorCancellationToken = true,
            AfterInviteChanged = operation == "accept"
                ? () => requestCancellation.Cancel()
                : null
        };
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OnRevocation = operation == "accept"
                ? null
                : requestCancellation.Cancel
        };
        var teamService = CreateTeamService(
            dbContext,
            eventPublisher: publisher,
            moduleEventPublisher: new NoopModuleEventPublisher(),
            realtimeConnectionManager: realtimeConnectionManager);

        switch (operation)
        {
            case "remove":
                await teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, affectedUser.Id, requestCancellation.Token);
                break;
            case "leave":
                await teamService.LeaveTeamAsync(affectedUser.Auth0UserId, team.Id, requestCancellation.Token);
                break;
            case "accept":
                await teamService.RespondToInviteAsync(affectedUser.Auth0UserId, invite!.Id, true, requestCancellation.Token);
                break;
            default:
                throw new InvalidOperationException($"Unknown membership operation '{operation}'.");
        }

        Assert.True(requestCancellation.IsCancellationRequested);
        var membershipEvent = Assert.Single(publisher.MembershipEvents);
        Assert.Equal(affectedUser.Id, membershipEvent.UserId);
        Assert.Equal(operation switch
        {
            "remove" => "Removed",
            "leave" => "Left",
            _ => "Joined"
        }, membershipEvent.Action);
        Assert.Equal(CancellationToken.None, Assert.Single(publisher.MembershipCancellationTokens));
        if (operation == "accept")
            Assert.Equal(CancellationToken.None, Assert.Single(publisher.InviteCancellationTokens));
    }

    [Fact]
    public async Task DeleteTeamAsync_AttemptsEveryRecipientBeforeSurfacingNotificationFailures()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var firstMember = CreateUser();
        var secondMember = CreateUser();
        var firstInvitee = CreateUser();
        var secondInvitee = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(firstMember.Id);
        team.AddMember(secondMember.Id);
        var invites = new[]
        {
            CreatePendingInvite(team, firstInvitee),
            CreatePendingInvite(team, secondInvitee)
        };
        dbContext.Users.AddRange(captain, firstMember, secondMember, firstInvitee, secondInvitee);
        dbContext.Teams.Add(team);
        dbContext.Set<TeamInvite>().AddRange(invites);
        await dbContext.SaveChangesAsync();

        var publishFailure = new IOException("planned first-recipient notification failure");
        var publisher = new ThrowingOnceTeamEventPublisher(publishFailure);
        var teamService = CreateTeamService(
            dbContext,
            eventPublisher: publisher,
            moduleEventPublisher: new NoopModuleEventPublisher());

        var exception = await Assert.ThrowsAsync<AggregateException>(() => teamService.DeleteTeamAsync(team.Id));

        Assert.Same(publishFailure, Assert.Single(exception.InnerExceptions));
        Assert.Equal(3, publisher.MembershipEvents.Count);
        Assert.Contains(publisher.MembershipEvents, evt => evt.UserId == captain.Id);
        Assert.Contains(publisher.MembershipEvents, evt => evt.UserId == firstMember.Id);
        Assert.Contains(publisher.MembershipEvents, evt => evt.UserId == secondMember.Id);
        Assert.Equal(2, publisher.InviteEvents.Count);
        Assert.All(invites, invite => Assert.Contains(
            publisher.InviteEvents,
            evt => evt.InviteId == invite.Id && evt.UserId == invite.UserId && evt.Status == nameof(TeamInviteStatus.Cancelled)));
        Assert.All(publisher.CancellationTokens, token => Assert.Equal(CancellationToken.None, token));
        Assert.True(team.IsDeleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteTeamAsync_WaitsForConcurrentInviteAndNotifiesItsRecipient(bool deleteByTeamId)
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var setupContext = new MercuriusDBContext(options);
        PostgresTestDatabase.Initialize(setupContext);

        var captain = CreateUser();
        var invitee = CreateUser();
        var team = CreateTeam("Alpha", captain);
        setupContext.Users.AddRange(captain, invitee);
        setupContext.Teams.Add(team);
        await setupContext.SaveChangesAsync();

        await using var inviteContext = new MercuriusDBContext(options);
        var inviteReachedIdentity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInvite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inviteIdentity = new DbContextIdentityModule(inviteContext)
        {
            BeforeUserProfileByIdAsync = async (userId, cancellationToken) =>
            {
                if (userId == new UserId(invitee.Id))
                {
                    inviteReachedIdentity.TrySetResult();
                    await releaseInvite.Task.WaitAsync(cancellationToken);
                }
            }
        };
        var inviteService = CreateTeamService(
            inviteContext,
            identityModuleOverride: inviteIdentity);
        var inviteTask = inviteService.InviteUserAsync(captain.Auth0UserId, team.Id, invitee.Id);
        await inviteReachedIdentity.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var lockInterceptor = new AdvisoryLockAttemptInterceptor();
        var deleteOptions = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(lockInterceptor)
            .Options;
        await using var deleteContext = new MercuriusDBContext(deleteOptions);
        var deletionEvents = new RecordingTeamEventPublisher();
        var deleteService = CreateTeamService(
            deleteContext,
            eventPublisher: deletionEvents,
            moduleEventPublisher: new NoopModuleEventPublisher());

        var deleteTask = deleteByTeamId
            ? deleteService.DeleteTeamAsync(team.Id)
            : deleteService.DeleteTeamAsync(captain.Auth0UserId, team.Id);
        var deleteWasBlockedByInvite = false;
        try
        {
            await lockInterceptor.LockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            deleteWasBlockedByInvite = !deleteTask.IsCompleted;
        }
        finally
        {
            releaseInvite.TrySetResult();
        }

        await inviteTask;
        await deleteTask;

        Assert.True(deleteWasBlockedByInvite, "Team deletion should wait for the in-flight invite transaction.");
        Assert.Contains(
            deletionEvents.InviteEvents,
            published => published.TeamId == team.Id &&
                published.UserId == invitee.Id &&
                published.Status == nameof(TeamInviteStatus.Cancelled));
        await using var verifyContext = new MercuriusDBContext(options);
        Assert.True(await verifyContext.Teams.AnyAsync(candidate => candidate.Id == team.Id && candidate.IsDeleted));
        Assert.False(await verifyContext.Set<TeamInvite>().AnyAsync(invite => invite.TeamId == team.Id));
    }

    [Fact]
    public async Task RespondToInviteAsync_WaitsForDeleteThenRejectsDeletedTeam()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var setupContext = new MercuriusDBContext(options);
        PostgresTestDatabase.Initialize(setupContext);

        var captain = CreateUser();
        var invitee = CreateUser();
        var team = CreateTeam("Alpha", captain);
        var invite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = invitee.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
        setupContext.Users.AddRange(captain, invitee);
        setupContext.Teams.Add(team);
        setupContext.Set<TeamInvite>().Add(invite);
        await setupContext.SaveChangesAsync();

        var deletePause = new TeamReadPauseInterceptor();
        var deleteOptions = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(deletePause)
            .Options;
        await using var deleteContext = new MercuriusDBContext(deleteOptions);
        var deleteService = CreateTeamService(
            deleteContext,
            moduleEventPublisher: new NoopModuleEventPublisher());
        var deleteTask = deleteService.DeleteTeamAsync(team.Id);
        await deletePause.TeamReadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var responseLockInterceptor = new AdvisoryLockAttemptInterceptor();
        var responseOptions = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(responseLockInterceptor)
            .Options;
        await using var responseContext = new MercuriusDBContext(responseOptions);
        var responseService = CreateTeamService(
            responseContext,
            moduleEventPublisher: new NoopModuleEventPublisher());
        var responseTask = responseService.RespondToInviteAsync(invitee.Auth0UserId, invite.Id, true);
        var responseWasBlockedByDelete = false;
        try
        {
            await responseLockInterceptor.LockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            responseWasBlockedByDelete = !responseTask.IsCompleted;
        }
        finally
        {
            deletePause.ReleaseTeamRead.TrySetResult();
        }

        await deleteTask;
        var responseException = await Record.ExceptionAsync(async () => await responseTask);

        Assert.True(responseWasBlockedByDelete, "Invite response should wait for the in-flight team deletion.");
        Assert.IsType<NotFoundException>(responseException);
        await using var verifyContext = new MercuriusDBContext(options);
        Assert.True(await verifyContext.Teams.AnyAsync(candidate => candidate.Id == team.Id && candidate.IsDeleted));
        Assert.False(await verifyContext.Set<TeamInvite>().AnyAsync(candidate => candidate.Id == invite.Id));
        Assert.False(await verifyContext.Set<TeamMember>().AnyAsync(member => member.TeamId == team.Id && member.UserId == invitee.Id));
    }

    [Fact]
    public async Task InviteMaintenance_WaitsForDeleteThenRechecksExpiredInvites()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var setupContext = new MercuriusDBContext(options);
        PostgresTestDatabase.Initialize(setupContext);

        var captain = CreateUser();
        var invitee = CreateUser();
        var team = CreateTeam("Alpha", captain);
        var invite = new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = invitee.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow.AddDays(-20),
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        };
        setupContext.Users.AddRange(captain, invitee);
        setupContext.Teams.Add(team);
        setupContext.Set<TeamInvite>().Add(invite);
        await setupContext.SaveChangesAsync();

        var deletePause = new TeamReadPauseInterceptor();
        var deleteOptions = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(deletePause)
            .Options;
        await using var deleteContext = new MercuriusDBContext(deleteOptions);
        var deleteService = CreateTeamService(
            deleteContext,
            eventPublisher: new RecordingTeamEventPublisher(),
            moduleEventPublisher: new NoopModuleEventPublisher());
        var deleteTask = deleteService.DeleteTeamAsync(team.Id);
        await deletePause.TeamReadAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var maintenanceLockInterceptor = new AdvisoryLockAttemptInterceptor();
        var maintenanceOptions = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(maintenanceLockInterceptor)
            .Options;
        await using var maintenanceContext = new MercuriusDBContext(maintenanceOptions);
        var maintenanceEvents = new RecordingTeamEventPublisher();
        var maintenanceService = new TeamInviteMaintenanceService(
            new TeamsDbContextAdapter<MercuriusDBContext>(maintenanceContext),
            maintenanceEvents,
            Options.Create(new TeamInviteMaintenanceOptions
            {
                RetentionDays = 90,
                MaintenanceBatchSize = 10,
                MaintenanceEventConcurrency = 2
            }));
        var maintenanceTask = maintenanceService.RunBatchAsync();
        var maintenanceWasBlockedByDelete = false;
        try
        {
            await maintenanceLockInterceptor.LockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            maintenanceWasBlockedByDelete = !maintenanceTask.IsCompleted;
        }
        finally
        {
            deletePause.ReleaseTeamRead.TrySetResult();
        }

        await deleteTask;
        var maintenanceCount = await maintenanceTask;

        Assert.True(maintenanceWasBlockedByDelete, "Invite maintenance should wait for the in-flight deletion of the team.");
        Assert.Equal(0, maintenanceCount);
        Assert.Empty(maintenanceEvents.InviteEvents);
        await using var verifyContext = new MercuriusDBContext(options);
        Assert.True(await verifyContext.Teams.AnyAsync(candidate => candidate.Id == team.Id && candidate.IsDeleted));
        Assert.False(await verifyContext.Set<TeamInvite>().AnyAsync(candidate => candidate.Id == invite.Id));
    }

    [Fact]
    public async Task DeleteTeamAsync_RetiresUnreferencedLogoAfterCommitAndGroupRevocation()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var operationOrder = new List<string>();
        var mediaModule = new StubMediaModule("/images/unused.webp")
        {
            OperationOrder = operationOrder
        };
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OperationOrder = operationOrder
        };
        var teamService = CreateTeamService(
            dbContext,
            mediaModule,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        await teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id);

        Assert.Equal(["RevokeGroup", "DeleteImage"], operationOrder);
        var deletedImage = Assert.Single(mediaModule.DeletedImages);
        Assert.Equal("/images/original.webp", deletedImage.ImageUrl);
        Assert.Equal(CancellationToken.None, deletedImage.CancellationToken);
        dbContext.ChangeTracker.Clear();
        Assert.True((await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).IsDeleted);
        Assert.Contains(
            await dbContext.OutboxMessages.Select(message => message.EventType).ToListAsync(),
            eventType => eventType == typeof(TeamDeletedIntegrationEvent).FullName);
    }

    [Fact]
    public async Task DeleteTeamAsync_DurableEventFailureNeverRetiresLogoOrRevokesGroup()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var mediaModule = new StubMediaModule("/images/unused.webp");
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager();
        var teamService = CreateTeamService(
            dbContext,
            mediaModule,
            moduleEventPublisher: new ThrowingModuleEventPublisher(),
            realtimeConnectionManager: realtimeConnectionManager);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id));

        Assert.Empty(mediaModule.DeletedImages);
        Assert.Empty(realtimeConnectionManager.GroupRevocations);
    }

    [Fact]
    public async Task DeleteTeamAsync_PostCommitLogoDeleteFailureIsLoggedAndNonFatal()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var operationOrder = new List<string>();
        var mediaModule = new StubMediaModule("/images/unused.webp")
        {
            DeleteException = new IOException("planned delete failure"),
            OperationOrder = operationOrder
        };
        var logger = new RecordingLogger<TeamService>();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OperationOrder = operationOrder
        };
        var teamService = CreateTeamService(
            dbContext,
            mediaModule,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager,
            logger: logger);

        await teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id);

        Assert.Equal(["RevokeGroup", "DeleteImage"], operationOrder);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Exception is IOException &&
            entry.Message.Contains("retire an unreferenced Team logo", StringComparison.Ordinal));
        dbContext.ChangeTracker.Clear();
        Assert.True((await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).IsDeleted);
    }

    [Fact]
    public async Task DeleteTeamAsync_PostCommitRevocationFailureStillAttemptsLogoRetirement()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var operationOrder = new List<string>();
        var mediaModule = new StubMediaModule("/images/unused.webp")
        {
            OperationOrder = operationOrder
        };
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            OperationOrder = operationOrder,
            ThrowOnRevocation = true
        };
        var teamService = CreateTeamService(
            dbContext,
            mediaModule,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            teamService.DeleteTeamAsync(captain.Auth0UserId, team.Id));

        Assert.Equal("planned post-commit revocation failure", exception.Message);
        Assert.Equal(["RevokeGroup", "DeleteImage"], operationOrder);
        Assert.Single(mediaModule.DeletedImages);
        dbContext.ChangeTracker.Clear();
        Assert.True((await dbContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).IsDeleted);
    }

    [Fact]
    public async Task RemoveTeamLogoAsync_ReferenceQueryFailureRetainsLogoAndLogsWarning()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.LogoUrl = "/images/original.webp";
        dbContext.Users.Add(captain);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var mediaModule = new StubMediaModule("/images/unused.webp");
        var tournamentReadService = new StubTeamTournamentReadService(dbContext)
        {
            ThrowOnLogoReferenceQuery = true
        };
        var logger = new RecordingLogger<TeamService>();
        var teamService = CreateTeamService(
            dbContext,
            mediaModule,
            tournamentReadService: tournamentReadService,
            logger: logger);

        await teamService.RemoveTeamLogoAsync(captain.Auth0UserId, team.Id);

        Assert.Empty(mediaModule.DeletedImages);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("file was retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveMemberAsync_PostCommitRevocationFailureLeavesMutationCommitted()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.Add(team);
        await dbContext.SaveChangesAsync();
        var realtimeConnectionManager = new RecordingRealtimeConnectionManager
        {
            ThrowOnRevocation = true
        };
        var publisher = new RecordingTeamEventPublisher();
        var teamService = CreateTeamService(
            dbContext,
            eventPublisher: publisher,
            moduleEventPublisher: new ModuleEventPublisher(dbContext),
            realtimeConnectionManager: realtimeConnectionManager);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            teamService.RemoveMemberAsync(captain.Auth0UserId, team.Id, member.Id));

        Assert.Equal("planned post-commit revocation failure", exception.Message);
        dbContext.ChangeTracker.Clear();
        var persistedTeam = await dbContext.Teams
            .Include(candidate => candidate.Members)
            .SingleAsync(candidate => candidate.Id == team.Id);
        Assert.DoesNotContain(persistedTeam.Members, candidate => candidate.UserId == member.Id);
        Assert.Empty(publisher.MembershipEvents);
    }

    [Fact]
    public async Task RealtimeTeamEventPublisher_PushesInviteEventsToUserGroup()
    {
        var teamId = Guid.NewGuid();
        var inviteId = Guid.NewGuid();
        var affectedUserId = Guid.NewGuid();
        var hubContext = new RecordingHubContext();
        var realtimePublisher = new SignalRRealtimePublisher<TeamManagementHub>(hubContext);
        var publisher = new RealtimeTeamEventPublisher(realtimePublisher);

        await publisher.InviteChangedAsync(teamId, inviteId, affectedUserId, "Pending");

        var send = Assert.Single(hubContext.HubClients.Proxy.Sends);
        Assert.Equal("TeamInviteChanged", send.Method);
        Assert.Equal([TeamRealtimeGroups.GetUserGroup(affectedUserId)], hubContext.HubClients.RecordedGroups);
        var payload = Assert.IsType<TeamInviteChangedRealtimeEvent>(Assert.Single(send.Args));
        Assert.Equal(teamId, payload.TeamId);
        Assert.Equal(inviteId, payload.InviteId);
        Assert.Equal(affectedUserId, payload.UserId);
        Assert.Equal("Pending", payload.Status);
    }

    [Fact]
    public async Task TeamRealtimeAuthorizer_AllowsOnlyActiveTeamMembers()
    {
        await using var dbContext = CreateDbContext();
        var captain = CreateUser();
        var member = CreateUser();
        var outsider = CreateUser();
        var deletedCaptain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        var deletedTeam = CreateTeam("Deleted", deletedCaptain);
        team.AddMember(member.Id);
        deletedTeam.Delete(DateTime.UtcNow);
        dbContext.Users.AddRange(captain, member, outsider, deletedCaptain);
        dbContext.Teams.AddRange(team, deletedTeam);
        await dbContext.SaveChangesAsync();
        var authorizer = new EfTeamRealtimeAuthorizer(new TeamsDbContextAdapter<MercuriusDBContext>(dbContext));

        Assert.True(await authorizer.CanSubscribeToTeamAsync(new TeamId(team.Id), new UserId(captain.Id)));
        Assert.True(await authorizer.CanSubscribeToTeamAsync(new TeamId(team.Id), new UserId(member.Id)));
        Assert.False(await authorizer.CanSubscribeToTeamAsync(new TeamId(team.Id), new UserId(outsider.Id)));
        Assert.False(await authorizer.CanSubscribeToTeamAsync(new TeamId(deletedTeam.Id), new UserId(deletedCaptain.Id)));
    }

    [Fact]
    public async Task SaveChangesAsync_WhenTeamVersionChangedConcurrently_ThrowsConflictAndKeepsVersionsUnique()
    {
        await using var database = PostgresTestDatabase.Create();
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql(database.ConnectionString)
            .Options;
        await using var setupContext = new MercuriusDBContext(options);
        PostgresTestDatabase.Initialize(setupContext);
        var captain = CreateUser();
        var team = CreateTeam("Alpha", captain);
        setupContext.Users.Add(captain);
        setupContext.Teams.Add(team);
        await setupContext.SaveChangesAsync();
        await using var firstContext = new MercuriusDBContext(options);
        await using var secondContext = new MercuriusDBContext(options);
        var firstTeam = await firstContext.Teams.SingleAsync(candidate => candidate.Id == team.Id);
        var secondTeam = await secondContext.Teams.SingleAsync(candidate => candidate.Id == team.Id);
        firstTeam.Version++;
        secondTeam.Version++;

        await new TeamsDbContextAdapter<MercuriusDBContext>(firstContext).SaveChangesAsync();
        var conflict = await Assert.ThrowsAsync<ConflictException>(() =>
            new TeamsDbContextAdapter<MercuriusDBContext>(secondContext).SaveChangesAsync());

        Assert.Equal("team_changed", conflict.Code);
        setupContext.ChangeTracker.Clear();
        Assert.Equal(1, (await setupContext.Teams.SingleAsync(candidate => candidate.Id == team.Id)).Version);
    }

    [Fact]
    public async Task DeleteUserByIdAsync_WithCrossModuleReferences_AnonymizesUserWithoutHardDelete()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var (member, _) = await SeedCrossModuleReferencesAsync(dbContext);

        await new Mercurius.Modules.Identity.Services.UserService(dbContext, null!).DeleteUserByIdAsync(member.Id);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Users.AnyAsync(user => user.Id == member.Id && user.IsDeleted));
        Assert.True(await dbContext.Set<PlacementUser>().AnyAsync(placementUser => placementUser.UserId == member.Id));
        var hardDelete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            dbContext.Users.Where(user => user.Id == member.Id).ExecuteDeleteAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, hardDelete.SqlState);
    }

    [Fact]
    public async Task DeleteTeamAsync_WithCrossModuleReferences_SoftDeletesTeamWithoutHardDelete()
    {
        await using var dbContext = PostgresTestDatabase.CreateDbContext();
        var (_, team) = await SeedCrossModuleReferencesAsync(dbContext);

        await CreateTeamService(dbContext).DeleteTeamAsync(team.Id);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Teams.AnyAsync(candidate => candidate.Id == team.Id && candidate.IsDeleted));
        Assert.True(await dbContext.Set<PlacementTeam>().AnyAsync(placementTeam => placementTeam.TeamId == team.Id));
        var hardDelete = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            dbContext.Teams.Where(candidate => candidate.Id == team.Id).ExecuteDeleteAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, hardDelete.SqlState);
    }

    private static async Task<(User Member, Team Team)> SeedCrossModuleReferencesAsync(MercuriusDBContext dbContext)
    {
        var captain = CreateUser();
        var member = CreateUser();
        var team = CreateTeam("Alpha", captain);
        team.AddMember(member.Id);
        var otherTeam = CreateTeam("Bravo", captain);
        var tournament = new TournamentAggregate("Tournament", BracketType.SingleElimination, GameFormat.BestOf1, GameFormat.BestOf1, ParticipationMode.Team, 2)
        {
            Id = Guid.NewGuid()
        };
        dbContext.Users.AddRange(captain, member);
        dbContext.Teams.AddRange(team, otherTeam);
        dbContext.Set<TeamInvite>().Add(CreatePendingInvite(otherTeam, member));
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<Placement>().Add(new Placement
        {
            Id = Guid.NewGuid(),
            Place = 1,
            TournamentId = tournament.Id,
            Users = [new PlacementUser { UserId = member.Id }],
            Teams = [new PlacementTeam { TeamId = team.Id }]
        });
        await dbContext.SaveChangesAsync();
        return (member, team);
    }

    private static User CreateUser()
    {
        var id = Interlocked.Increment(ref _nextId);
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|user{id}",
            Username = $"user{id}",
            Firstname = $"First{id}",
            Lastname = $"Last{id}",
            Email = $"user{id}@example.com",
            DiscordId = $"discord-{id}",
            SteamId = $"steam-{id}",
            RiotId = $"riot-{id}"
        };
    }

    private static Team CreateTeam()
    {
        return CreateTeam("Test Team", CreateUser());
    }

    private static Team CreateTeam(string name, User captain)
    {
        var team = new Team(name, captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        return team;
    }

    private static TeamInvite CreatePendingInvite(Team team, User user)
    {
        return new TeamInvite
        {
            Id = Guid.NewGuid(),
            Team = team,
            TeamId = team.Id,
            UserId = user.Id,
            Status = TeamInviteStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        };
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static void AddTeamRegistration(
        MercuriusDBContext dbContext,
        TournamentAggregate tournament,
        Team team,
        User captain,
        IReadOnlyCollection<User> rosterMembers,
        TournamentRegistrationStatus status)
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
            TeamLogoUrlAtRegistration = team.LogoUrl,
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
                    : RosterMemberConfirmationStatus.Confirmed,
                ConfirmedAtUtc = DateTime.UtcNow
            }).ToList()
        });
    }

    private static UniqueConstraintDbContext CreateUniqueConstraintDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new UniqueConstraintDbContext(options);
    }

    private static TeamEventPublishingDecorator CreateTeamService(
        MercuriusDBContext dbContext,
        IMediaModule? mediaModule = null,
        ITeamEventPublisher? eventPublisher = null,
        IModuleEventPublisher? moduleEventPublisher = null,
        IRealtimeConnectionManager? realtimeConnectionManager = null,
        ITeamTournamentReadService? tournamentReadService = null,
        ILogger<TeamService>? logger = null,
        IIdentityModule? identityModuleOverride = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["TeamInvite:ExpirationDays"] = "14",
                ["TeamInvite:RetentionDays"] = "90",
                ["TeamInvite:DeclinedResendLimit"] = "3"
            })
            .Build();
        var identityModule = identityModuleOverride ?? new DbContextIdentityModule(dbContext);
        var teamsDbContext = new TeamsDbContextAdapter<MercuriusDBContext>(dbContext);

        return new TeamEventPublishingDecorator(
            new TeamService(
                teamsDbContext,
                configuration,
                identityModule,
                mediaModule ?? new StubMediaModule("https://example.test/default-team-logo.webp"),
                tournamentReadService ?? new StubTeamTournamentReadService(dbContext),
                logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamService>.Instance),
            teamsDbContext,
            identityModule,
            eventPublisher ?? new NoopTeamEventPublisher(),
            moduleEventPublisher ?? new NoopModuleEventPublisher(),
            realtimeConnectionManager ?? new NoopRealtimeConnectionManager());
    }

    private static TeamService CreateTeamQueryService(
        MercuriusDBContext dbContext,
        Mercurius.Modules.Identity.Contracts.IIdentityModule identityModule)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TeamInvite:ResendCooldownDays"] = "7",
                ["TeamInvite:ExpirationDays"] = "14",
                ["TeamInvite:RetentionDays"] = "90",
                ["TeamInvite:DeclinedResendLimit"] = "3"
            })
            .Build();

        return new TeamService(
            new TeamsDbContextAdapter<MercuriusDBContext>(dbContext),
            configuration,
            identityModule,
            new StubMediaModule("https://example.test/default-team-logo.webp"),
            new StubTeamTournamentReadService(dbContext),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamService>.Instance);
    }

    private static IFormFile CreateFormFile(string contentType = "image/png")
    {
        var bytes = new byte[] { 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "logo", "logo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private sealed class StubMediaModule : IMediaModule
    {
        private readonly string _imageUrl;

        public StubMediaModule(string imageUrl)
        {
            _imageUrl = imageUrl;
        }

        public CancellationTokenSource? CancelAfterSave { get; init; }

        public Exception? DeleteException { get; init; }

        public List<string>? OperationOrder { get; init; }

        public List<DeletedImage> DeletedImages { get; } = [];

        public Task<StoredMediaAsset> SaveImageAsync(
            MediaUpload upload,
            CancellationToken cancellationToken = default)
        {
            CancelAfterSave?.Cancel();
            return Task.FromResult(new StoredMediaAsset(_imageUrl));
        }

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        {
            DeletedImages.Add(new DeletedImage(imageUrl, cancellationToken));
            OperationOrder?.Add("DeleteImage");
            return DeleteException is null
                ? Task.CompletedTask
                : Task.FromException(DeleteException);
        }
    }

    private sealed record DeletedImage(string? ImageUrl, CancellationToken CancellationToken);

    private sealed class StubTeamTournamentReadService(MercuriusDBContext dbContext) : ITeamTournamentReadService
    {
        public bool ThrowOnLogoReferenceQuery { get; init; }

        public async Task<IReadOnlyList<PublicTeamTournamentSummary>> GetPublicTeamTournamentsAsync(Guid teamId, CancellationToken cancellationToken = default)
        {
            return await dbContext.Set<TournamentRegistration>()
                .AsNoTracking()
                .Where(registration => registration.TeamId == teamId && registration.Status == TournamentRegistrationStatus.Active)
                .Select(registration => new PublicTeamTournamentSummary(new TournamentId(registration.TournamentId), registration.Tournament.Name))
                .OrderBy(tournament => tournament.Name)
                .ThenBy(tournament => tournament.TournamentId.Value)
                .ToListAsync(cancellationToken);
        }

        public Task<bool> IsUserInProtectedTournamentRosterAsync(Guid teamId, Guid userId, CancellationToken cancellationToken = default)
        {
            return dbContext.Set<TournamentRegistration>()
                .AsNoTracking()
                .Where(registration =>
                    registration.TeamId == teamId &&
                    registration.Tournament.Status == TournamentStatus.InProgress &&
                    registration.RosterMembers.Any(member => member.UserId == userId))
                .AnyAsync(cancellationToken);
        }

        public Task<bool> IsTeamInDeleteBlockingTournamentAsync(Guid teamId, CancellationToken cancellationToken = default)
        {
            return dbContext.Set<TournamentRegistration>()
                .AsNoTracking()
                .Where(registration =>
                    registration.TeamId == teamId &&
                    (registration.Tournament.Status == TournamentStatus.Scheduled || registration.Tournament.Status == TournamentStatus.InProgress))
                .AnyAsync(cancellationToken);
        }

        public Task<bool> IsTeamLogoReferencedAsync(string logoUrl, CancellationToken cancellationToken = default)
        {
            if (ThrowOnLogoReferenceQuery)
                return Task.FromException<bool>(new InvalidOperationException("planned reference query failure"));

            return dbContext.Set<TournamentRegistration>()
                .AsNoTracking()
                .AnyAsync(
                    registration => registration.TeamLogoUrlAtRegistration == logoUrl,
                    cancellationToken);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingTeamEventPublisher : ITeamEventPublisher
    {
        private readonly List<string>? _operationOrder;

        public RecordingTeamEventPublisher(List<string>? operationOrder = null)
        {
            _operationOrder = operationOrder;
        }

        public List<RecordedInviteEvent> InviteEvents { get; } = [];
        public List<RecordedMembershipEvent> MembershipEvents { get; } = [];
        public List<RecordedCaptainEvent> CaptainEvents { get; } = [];
        public List<CancellationToken> InviteCancellationTokens { get; } = [];
        public List<CancellationToken> MembershipCancellationTokens { get; } = [];
        public bool HonorCancellationToken { get; init; }
        public Action? AfterInviteChanged { get; init; }

        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default)
        {
            InviteEvents.Add(new RecordedInviteEvent(teamId, inviteId, affectedUserId, status));
            InviteCancellationTokens.Add(cancellationToken);
            AfterInviteChanged?.Invoke();
            if (HonorCancellationToken)
                cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default)
        {
            MembershipEvents.Add(new RecordedMembershipEvent(teamId, affectedUserId, action));
            MembershipCancellationTokens.Add(cancellationToken);
            _operationOrder?.Add("MembershipBroadcast");
            if (HonorCancellationToken)
                cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default)
        {
            CaptainEvents.Add(new RecordedCaptainEvent(teamId, newCaptainUserId));
            return Task.CompletedTask;
        }

        public sealed record RecordedInviteEvent(Guid TeamId, Guid InviteId, Guid UserId, string Status);
        public sealed record RecordedMembershipEvent(Guid TeamId, Guid UserId, string Action);
        public sealed record RecordedCaptainEvent(Guid TeamId, Guid NewCaptainUserId);
    }

    private sealed class NoopTeamEventPublisher : ITeamEventPublisher
    {
        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull => Guid.NewGuid();
    }

    private sealed class ThrowingOnceTeamEventPublisher(Exception firstFailure) : ITeamEventPublisher
    {
        private int _membershipEventCount;

        public List<RecordedInviteEvent> InviteEvents { get; } = [];
        public List<RecordedMembershipEvent> MembershipEvents { get; } = [];
        public List<CancellationToken> CancellationTokens { get; } = [];

        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default)
        {
            InviteEvents.Add(new RecordedInviteEvent(teamId, inviteId, affectedUserId, status));
            CancellationTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }

        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default)
        {
            MembershipEvents.Add(new RecordedMembershipEvent(teamId, affectedUserId, action));
            CancellationTokens.Add(cancellationToken);
            return Interlocked.Increment(ref _membershipEventCount) == 1
                ? Task.FromException(firstFailure)
                : Task.CompletedTask;
        }

        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public sealed record RecordedInviteEvent(Guid TeamId, Guid InviteId, Guid UserId, string Status);
        public sealed record RecordedMembershipEvent(Guid TeamId, Guid UserId, string Action);
    }

    private sealed class AdvisoryLockAttemptInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource LockAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase))
                LockAttempted.TrySetResult();

            return ValueTask.FromResult(result);
        }
    }

    private sealed class TeamReadPauseInterceptor : DbCommandInterceptor
    {
        private int _paused;

        public TaskCompletionSource TeamReadAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseTeamRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if ((command.CommandText.Contains("\"teams\".\"teams\"", StringComparison.OrdinalIgnoreCase) ||
                 command.CommandText.Contains("teams.teams", StringComparison.OrdinalIgnoreCase)) &&
                Interlocked.Exchange(ref _paused, 1) == 0)
            {
                TeamReadAttempted.TrySetResult();
                await ReleaseTeamRead.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class ThrowingInviteChangedTeamEventPublisher : ITeamEventPublisher
    {
        public Task InviteChangedAsync(Guid teamId, Guid inviteId, Guid affectedUserId, string status, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Invite publish failed.");
        }

        public Task MembershipChangedAsync(Guid teamId, Guid affectedUserId, string action, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CaptainTransferredAsync(Guid teamId, Guid newCaptainUserId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingModuleEventPublisher : IModuleEventPublisher
    {
        public Guid Publish<TPayload>(TPayload payload, DateTime? occurredAtUtc = null)
            where TPayload : notnull
        {
            throw new InvalidOperationException("Durable event publish failed.");
        }
    }

    private sealed class UniqueConstraintDbContext(DbContextOptions<MercuriusDBContext> options) : MercuriusDBContext(options)
    {
        public bool ThrowTeamNameUniqueConstraint { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowTeamNameUniqueConstraint)
                throw new DbUpdateException(
                    "Unique constraint violation.",
                    new InvalidOperationException("IX_Teams_NormalizedName"));

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class RecordingHubContext : IHubContext<TeamManagementHub>
    {
        public RecordingHubClients HubClients { get; } = new();
        IHubClients IHubContext<TeamManagementHub>.Clients => HubClients;
        public IGroupManager Groups { get; } = new NoopGroupManager();
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public RecordingClientProxy Proxy { get; } = new();
        public IReadOnlyList<string> RecordedGroups { get; private set; } = [];

        public IClientProxy All => Proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Client(string connectionId) => Proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => Proxy;
        public IClientProxy Group(string groupName)
        {
            RecordedGroups = [groupName];
            return Proxy;
        }
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames)
        {
            RecordedGroups = groupNames;
            return Proxy;
        }
        public IClientProxy User(string userId) => Proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => Proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<RecordedSend> Sends { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sends.Add(new RecordedSend(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class NoopGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed record RecordedSend(string Method, object?[] Args);
}

