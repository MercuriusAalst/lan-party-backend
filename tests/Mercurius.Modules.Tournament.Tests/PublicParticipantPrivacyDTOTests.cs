using System.Text.Json;
using Mercurius.Modules.Teams.Application.DTOs;

namespace Mercurius.Modules.Tournament.Tests;

public class PublicParticipantPrivacyDTOTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TeamPublicUserDTO_ContainsOnlyPublicIdentityFields()
    {
        var json = Serialize(CreateUser(1).ToPublicUserDTO());

        Assert.Contains("\"id\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"username\":\"user1\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"displayName\":\"user1\"", json, StringComparison.OrdinalIgnoreCase);
        AssertPublicPlatformIdsAreAbsent(json);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetTournamentDTO_UsesPrivacySafeRegistrationParticipants()
    {
        var tournament = CreateTournament(ParticipationMode.Individual);
        var user = CreateUser(2);
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username ?? string.Empty,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });

        var json = Serialize(tournament.ToGetTournamentDTO([user]));

        Assert.Contains("\"registrations\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"users\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"teams\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"username\":\"user2\"", json, StringComparison.OrdinalIgnoreCase);
        AssertPublicPlatformIdsAreAbsent(json);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetPlacementDTO_UsesPrivacySafeTeamMembers()
    {
        var team = CreateTeam(3);
        var placement = new Placement
        {
            Place = 1,
            Teams = [new PlacementTeam { TeamId = team.Id }]
        };

        var json = Serialize(placement.ToGetPlacementDTO(teams: [team]));

        Assert.Contains("\"teams\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"members\":", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetPlacementDTO_DoesNotExposeDeletedTeamMemberLabels()
    {
        var team = CreateTeam(30);
        var deletedMember = CreateUser(31);
        deletedMember.IsDeleted = true;
        team.AddMember(deletedMember.Id);
        var placement = new Placement
        {
            Place = 1,
            Teams = [new PlacementTeam { TeamId = team.Id }]
        };

        var json = Serialize(placement.ToGetPlacementDTO(users: [deletedMember], teams: [team]));

        Assert.DoesNotContain("Deleted user", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetTeamDTO_OmitsInvitesAndUsesPrivacySafeMembers()
    {
        var team = CreateTeam(4);
        team.TeamInvites.Add(new TeamInvite
        {
            TeamId = team.Id,
            UserId = Guid.NewGuid(),
            Status = TeamInviteStatus.Pending
        });

        var json = Serialize(new GetTeamDTO
        {
            Id = team.Id,
            Name = team.Name,
            CaptainUserId = team.CaptainUserId ?? Guid.Empty,
            LogoUrl = team.LogoUrl,
            Members =
            [
                new TeamPublicUserDTO
                {
                    Id = Guid.NewGuid(),
                    Username = "public-member",
                    DisplayName = "public-member"
                }
            ]
        });

        Assert.Contains("\"members\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"teamInvites\":", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetTournamentDTO_ExposesPublicRegistrationRostersWithoutPrivateRegistrationMetadata()
    {
        var captain = CreateUser(5);
        var rosterMember = CreateUser(6);
        var team = new Team("Roster Team", captain.Id) { Id = Guid.NewGuid() };
        team.AddMember(captain.Id);
        team.AddMember(CreateUser(99).Id);
        var tournament = CreateTournament(ParticipationMode.Team);
        var registration = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = captain.Id,
            RegisteredByUsernameAtRegistration = captain.Username ?? string.Empty,
            TeamId = team.Id,
            TeamNameAtRegistration = team.Name,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            RosterMembers =
            [
                new TournamentRegistrationRosterMember
                {
                    Id = Guid.NewGuid(),
                    Tournament = tournament,
                    TournamentId = tournament.Id,
                    TeamId = team.Id,
                    TeamNameAtRegistration = team.Name,
                    UserId = captain.Id,
                    UsernameAtRegistration = captain.Username ?? string.Empty,
                    DisplayNameAtRegistration = captain.DisplayName,
                    IsCaptain = true,
                    ConfirmationStatus = RosterMemberConfirmationStatus.AutoConfirmed
                },
                new TournamentRegistrationRosterMember
                {
                    Id = Guid.NewGuid(),
                    Tournament = tournament,
                    TournamentId = tournament.Id,
                    TeamId = team.Id,
                    TeamNameAtRegistration = team.Name,
                    UserId = rosterMember.Id,
                    UsernameAtRegistration = rosterMember.Username ?? string.Empty,
                    DisplayNameAtRegistration = rosterMember.DisplayName,
                    ConfirmationStatus = RosterMemberConfirmationStatus.Confirmed
                }
            ]
        };
        tournament.TournamentRegistrations.Add(registration);
        var pendingUser = CreateUser(7);
        tournament.TournamentRegistrations.Add(new TournamentRegistration
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
            RosterMembers =
            [
                new TournamentRegistrationRosterMember
                {
                    Id = Guid.NewGuid(),
                    Tournament = tournament,
                    TournamentId = tournament.Id,
                    TeamId = team.Id,
                    TeamNameAtRegistration = team.Name,
                    UserId = pendingUser.Id,
                    UsernameAtRegistration = pendingUser.Username ?? string.Empty,
                    DisplayNameAtRegistration = pendingUser.DisplayName,
                    ConfirmationStatus = RosterMemberConfirmationStatus.Pending
                }
            ]
        });

        var json = Serialize(tournament.ToGetTournamentDTO([captain, rosterMember, pendingUser], [team]));

        Assert.Contains("\"registrations\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"rosterMembers\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"username\":\"user6\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"username\":\"user7\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"confirmationStatus\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"createdAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"updatedAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"confirmationInviteId\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"confirmationNotification", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    [Fact]
    public void GetTournamentDTO_FallsBackToUsernameOnlyForDeletedProfiles()
    {
        var deletedUser = CreateUser(8);
        deletedUser.IsDeleted = true;
        deletedUser.Username = null;
        deletedUser.NormalizedUsername = null;

        var tournament = CreateTournament(ParticipationMode.Individual);
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = deletedUser.Id,
            RegisteredByUsernameAtRegistration = "archived-user",
            UserId = deletedUser.Id,
            UsernameAtRegistration = "archived-user",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        var json = Serialize(tournament.ToGetTournamentDTO());

        Assert.Contains("\"username\":\"archived-user\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"displayName\":\"archived-user\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("First8", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Last8", json, StringComparison.OrdinalIgnoreCase);
        AssertPrivateUserFieldsAreAbsent(json);
    }

    private static string Serialize<T>(T dto)
    {
        return JsonSerializer.Serialize(dto, WebJson);
    }

    private static void AssertPrivateUserFieldsAreAbsent(string json)
    {
        Assert.DoesNotContain("\"email\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"firstname\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"lastname\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"auth0UserId\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"emailVerified\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"isDeleted\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"createdAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"updatedAtUtc\":", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPublicPlatformIdsAreAbsent(string json)
    {
        Assert.DoesNotContain("\"discordId\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"steamId\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"riotId\":", json, StringComparison.OrdinalIgnoreCase);
    }

    private static TournamentAggregate CreateTournament(ParticipationMode participationMode)
    {
        return new TournamentAggregate(
            "Public Privacy Tournament",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf1,
            participationMode,
            participationMode == ParticipationMode.Team ? 2 : null);
    }

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{id}",
            Username = $"user{id}",
            NormalizedUsername = $"user{id}",
            Firstname = $"First{id}",
            Lastname = $"Last{id}",
            Email = $"user{id}@example.com",
            DiscordId = $"discord-{id}",
            SteamId = $"steam-{id}",
            RiotId = $"riot-{id}"
        };
    }

    private static Team CreateTeam(int id)
    {
        var captain = CreateUser(id);
        var teammate = CreateUser(id + 10);
        var team = new Team($"Team {id}", captain.Id)
        {
            Id = Guid.NewGuid()
        };
        team.AddMember(captain.Id);
        team.AddMember(teammate.Id);
        return team;
    }
}
