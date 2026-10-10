using Mercurius.TestInfrastructure;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Shared.Exceptions;
using DataAnnotations = System.ComponentModel.DataAnnotations;

namespace Mercurius.Modules.Tournament.Tests;

public class TournamentTests
{
    private TournamentAggregate CreateTournament(
        string name = "Test Tournament",
        BracketType bracketType = BracketType.SingleElimination,
        GameFormat format = GameFormat.BestOf1,
        GameFormat finalsFormat = GameFormat.BestOf1,
        ParticipationMode participationMode = ParticipationMode.Individual,
        int? teamSize = null)
    {
        return new TournamentAggregate(name, bracketType, format, finalsFormat, participationMode, teamSize ?? (participationMode == ParticipationMode.Team ? 5 : null));
    }

    [Fact]
    public void Constructor_SetsPropertiesCorrectly()
    {
        var tournament = CreateTournament("LAN", BracketType.RoundRobin, GameFormat.BestOf3, GameFormat.BestOf5, ParticipationMode.Team, 5);

        Assert.Equal("LAN", tournament.Name);
        Assert.Equal(BracketType.RoundRobin, tournament.BracketType);
        Assert.Equal(GameFormat.BestOf3, tournament.Format);
        Assert.Equal(GameFormat.BestOf5, tournament.FinalsFormat);
        Assert.Equal(TournamentStatus.Scheduled, tournament.Status);
        Assert.Equal(ParticipationMode.Team, tournament.ParticipationMode);
        Assert.Equal(ParticipationMode.Team, tournament.ParticipationMode);
        Assert.NotNull(tournament.Placements);
        Assert.NotNull(tournament.Matches);
        Assert.NotNull(tournament.TournamentRegistrations);
    }

    [Fact]
    public void Update_UpdatesProperties_WhenStatusIsScheduled()
    {
        var tournament = CreateTournament();

        tournament.Update("Updated", BracketType.DoubleElimination, GameFormat.BestOf3, GameFormat.BestOf5, ParticipationMode.Team, 5, tournament.PlannedStartTime, tournament.AverageGameDurationMinutes, tournament.RoundBreakDurationMinutes);

        Assert.Equal("Updated", tournament.Name);
        Assert.Equal(BracketType.DoubleElimination, tournament.BracketType);
        Assert.Equal(GameFormat.BestOf3, tournament.Format);
        Assert.Equal(GameFormat.BestOf5, tournament.FinalsFormat);
        Assert.Equal(ParticipationMode.Team, tournament.ParticipationMode);
        Assert.Equal(5, tournament.TeamSize);
    }

    [Fact]
    public void Constructor_AcceptsMaximumTeamSize()
    {
        var tournament = CreateTournament(participationMode: ParticipationMode.Team, teamSize: TournamentAggregate.MaximumTeamSize);

        Assert.Equal(TournamentAggregate.MaximumTeamSize, tournament.TeamSize);
    }

    [Fact]
    public void Constructor_RejectsTeamSizeAboveMaximum()
    {
        var exception = Assert.Throws<ValidationException>(() =>
            CreateTournament(participationMode: ParticipationMode.Team, teamSize: TournamentAggregate.MaximumTeamSize + 1));

        Assert.Equal($"Team tournament size cannot exceed {TournamentAggregate.MaximumTeamSize}.", exception.Message);
    }

    [Fact]
    public void Update_RejectsTeamSizeAboveMaximum()
    {
        var tournament = CreateTournament(participationMode: ParticipationMode.Team, teamSize: 5);

        var exception = Assert.Throws<ValidationException>(() =>
            tournament.Update(
                tournament.Name,
                tournament.BracketType,
                tournament.Format,
                tournament.FinalsFormat,
                ParticipationMode.Team,
                TournamentAggregate.MaximumTeamSize + 1,
                tournament.PlannedStartTime,
                tournament.AverageGameDurationMinutes,
                tournament.RoundBreakDurationMinutes));

        Assert.Equal($"Team tournament size cannot exceed {TournamentAggregate.MaximumTeamSize}.", exception.Message);
    }

    [Theory]
    [InlineData((int)TournamentStatus.InProgress)]
    [InlineData((int)TournamentStatus.Completed)]
    public void Update_ThrowsException_WhenStatusIsInProgressOrCompleted(int status)
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, (TournamentStatus)status);

        Assert.Throws<ValidationException>(() =>
            tournament.Update("New", BracketType.Swiss, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Team, 5, tournament.PlannedStartTime, tournament.AverageGameDurationMinutes, tournament.RoundBreakDurationMinutes));
    }

    [Fact]
    public void Update_ThrowsException_WhenParticipationModeChangesAfterMatchesExist()
    {
        var tournament = CreateTournament();
        tournament.Matches.Add(new Match());

        var ex = Assert.Throws<ValidationException>(() =>
            tournament.Update("New", BracketType.Swiss, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Team, 5, tournament.PlannedStartTime, tournament.AverageGameDurationMinutes, tournament.RoundBreakDurationMinutes));

        Assert.Equal("Participation mode cannot be changed once registration or match generation has started.", ex.Message);
    }

    [Fact]
    public void Update_ThrowsException_WhenParticipationModeChangesAfterRegistrationsExist()
    {
        var tournament = CreateTournament();
        var user = CreateUser(1);
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

        var ex = Assert.Throws<ValidationException>(() =>
            tournament.Update("New", BracketType.Swiss, GameFormat.BestOf1, GameFormat.BestOf3, ParticipationMode.Team, 2, tournament.PlannedStartTime, tournament.AverageGameDurationMinutes, tournament.RoundBreakDurationMinutes));

        Assert.Equal("Participation mode cannot be changed once registration or match generation has started.", ex.Message);
    }

    [Fact]
    public void Cancel_SetsStatusToCanceled_WhenNotCompleted()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.InProgress);

        tournament.Cancel();

        Assert.Equal(TournamentStatus.Canceled, tournament.Status);
    }

    [Fact]
    public void Cancel_ThrowsValidationException_WhenStatusIsCompleted()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.Completed);

        Assert.Throws<ValidationException>(() => tournament.Cancel());
    }

    [Fact]
    public void Start_SetsStatusAndStartTime_WhenScheduledAndEnoughParticipants()
    {
        var tournament = CreateTournament();
        AddIndividualRegistration(tournament, CreateUser(1));
        AddIndividualRegistration(tournament, CreateUser(2));

        tournament.Start();

        Assert.Equal(TournamentStatus.InProgress, tournament.Status);
        Assert.True(tournament.StartTime <= DateTime.UtcNow && tournament.StartTime > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Start_ThrowsException_WhenNotScheduled()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.InProgress);
        AddIndividualRegistration(tournament, CreateUser(1));
        AddIndividualRegistration(tournament, CreateUser(2));

        Assert.Throws<ValidationException>(() => tournament.Start());
    }

    [Fact]
    public void Start_ThrowsException_WhenNotEnoughParticipants()
    {
        var tournament = CreateTournament();
        AddIndividualRegistration(tournament, CreateUser(1));

        Assert.Throws<ValidationException>(() => tournament.Start());
    }

    [Fact]
    public void Complete_SetsStatusAndEndTime_WhenInProgress()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.InProgress);

        tournament.Complete();

        Assert.Equal(TournamentStatus.Completed, tournament.Status);
        Assert.True(tournament.EndTime <= DateTime.UtcNow && tournament.EndTime > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Complete_ThrowsException_WhenNotInProgress()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.Scheduled);

        Assert.Throws<ValidationException>(() => tournament.Complete());
    }

    [Theory]
    [InlineData((int)TournamentStatus.Completed)]
    [InlineData((int)TournamentStatus.Canceled)]
    public void Reset_SetsStatusAndClearsCollections_WhenCompletedOrCanceled(int status)
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, (TournamentStatus)status);
        tournament.Set(x => x.StartTime, DateTime.UtcNow);
        tournament.Set(x => x.EndTime, DateTime.UtcNow);
        tournament.Matches.Add(new Match());

        tournament.Reset();

        Assert.Equal(TournamentStatus.Scheduled, tournament.Status);
        Assert.Equal(DateTime.MinValue, tournament.StartTime);
        Assert.Equal(DateTime.MinValue, tournament.EndTime);
        Assert.Empty(tournament.Matches);
    }

    [Fact]
    public void Reset_ThrowsException_WhenNotCompletedOrCanceled()
    {
        var tournament = CreateTournament();
        tournament.Set(x => x.Status, TournamentStatus.InProgress);

        Assert.Throws<ValidationException>(() => tournament.Reset());
    }

    [Fact]
    public void CreateTournamentDTO_FailsValidation_WhenParticipationModeIsMissing()
    {
        var dto = new CreateTournamentDTO
        {
            Name = "Test Tournament"
        };
        var validationContext = new DataAnnotations.ValidationContext(dto);
        var validationResults = new List<DataAnnotations.ValidationResult>();

        var isValid = DataAnnotations.Validator.TryValidateObject(dto, validationContext, validationResults, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(CreateTournamentDTO.ParticipationMode)));
    }

    [Fact]
    public void CreateTournamentDTO_FailsValidation_WhenRequiredFieldsAreMissing()
    {
        var dto = new CreateTournamentDTO();
        var validationContext = new DataAnnotations.ValidationContext(dto);
        var validationResults = new List<DataAnnotations.ValidationResult>();

        var isValid = DataAnnotations.Validator.TryValidateObject(dto, validationContext, validationResults, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(CreateTournamentDTO.Name)));
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(CreateTournamentDTO.ParticipationMode)));
        Assert.Contains(validationResults, r => r.MemberNames.Contains(nameof(CreateTournamentDTO.Image)));
    }

    private static User CreateUser(int id)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = "user" + id,
            Firstname = "First",
            Lastname = "Last",
            Email = $"user{id}@example.com"
        };
    }

    private static void AddIndividualRegistration(TournamentAggregate tournament, User user)
    {
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
    }

    private static Team CreateTeam(int id)
    {
        var captain = CreateUser(id);
        var team = new Team($"Team {id}", captain.Id)
        {
            Id = Guid.NewGuid()
        };
        team.AddMember(captain.Id);
        return team;
    }
}
