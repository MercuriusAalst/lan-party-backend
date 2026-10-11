using System.Reflection;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament;
using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Application.DTOs.Tournaments;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Platform.Eventing;

namespace Mercurius.Modules.Tournament.Tests;

public class TournamentPerformanceRegressionTests
{
    [Fact]
    public async Task GetAllTournamentsAsync_ReturnsContractCompatibleSummary_WhileDetailRetainsFullGraph()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("Alpha");
        var tournament = CreateTournament("Summary vs detail");
        tournament.Matches.Add(new Match
        {
            TournamentId = tournament.Id,
            RoundNumber = 1,
            MatchNumber = 1,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual
        });
        tournament.Placements.Add(new Placement
        {
            Place = 1,
            Users =
            [
                new PlacementUser { UserId = user.Id }
            ]
        });
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = user.Id,
            RegisteredByUsernameAtRegistration = user.Username ?? string.Empty,
            UserId = user.Id,
            UsernameAtRegistration = user.Username
        });

        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();

        var sponsorPlacement = CreateSponsorPlacement(tournament.Id);
        var service = CreateTournamentService(dbContext, [user], sponsorPlacement);

        var listItem = Assert.Single(await service.GetAllTournamentsAsync(1, 20));
        var detailItem = await service.GetTournamentByIdAsync(tournament.Id);

        Assert.Equal(tournament.Id, listItem.Id);
        Assert.Empty(listItem.Matches);
        Assert.Empty(listItem.Placements);
        Assert.Empty(listItem.Registrations);
        Assert.Equal(sponsorPlacement.Headline, listItem.SponsorPlacement?.Headline);

        Assert.Single(detailItem.Matches);
        Assert.Single(detailItem.Placements);
        Assert.Single(detailItem.Registrations);
        Assert.Equal(user.Username, detailItem.Registrations.Single().User?.Username);
        Assert.Equal(sponsorPlacement.Headline, detailItem.SponsorPlacement?.Headline);
    }

    [Fact]
    public async Task GetAllTournamentsAsync_PagesAfterDeterministicOrdering_AndBatchesEnrichment()
    {
        await using var dbContext = CreateDbContext();
        var first = CreateTournament("Zulu");
        first.Id = Guid.Parse("00000000-0000-0000-0000-000000000004");
        first.PlannedStartTime = new DateTime(2026, 8, 2, 18, 0, 0, DateTimeKind.Utc);
        var second = CreateTournament("Beta");
        second.Id = Guid.Parse("00000000-0000-0000-0000-000000000003");
        second.PlannedStartTime = new DateTime(2026, 8, 1, 18, 0, 0, DateTimeKind.Utc);
        var third = CreateTournament("Alpha");
        third.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        third.PlannedStartTime = new DateTime(2026, 8, 1, 18, 0, 0, DateTimeKind.Utc);
        var fourth = CreateTournament("Alpha");
        fourth.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        fourth.PlannedStartTime = new DateTime(2026, 8, 1, 18, 0, 0, DateTimeKind.Utc);
        dbContext.Set<TournamentAggregate>().AddRange(first, second, third, fourth);
        await dbContext.SaveChangesAsync();
        var sponsorshipModule = new StaticSponsorshipModule(null);
        var service = CreateTournamentService(dbContext, [], sponsorPlacement: null, sponsorshipModule);

        var page = await service.GetAllTournamentsAsync(2, 2);

        Assert.Equal([second.Id, first.Id], page.Select(tournament => tournament.Id).ToArray());
        Assert.Equal(1, sponsorshipModule.BatchCallCount);
        Assert.Equal([second.Id, first.Id], sponsorshipModule.LastBatchTournamentIds.Select(tournamentId => tournamentId.Value).ToArray());
    }

    [Fact]
    public async Task GetAllTournamentsAsync_OverflowingOffset_ReturnsEmptyWithoutEnrichment()
    {
        await using var dbContext = CreateDbContext();
        var sponsorshipModule = new StaticSponsorshipModule(null);
        var service = CreateTournamentService(dbContext, [], sponsorPlacement: null, sponsorshipModule);

        var page = await service.GetAllTournamentsAsync(int.MaxValue, 50);

        Assert.Empty(page);
        Assert.Equal(0, sponsorshipModule.BatchCallCount);
    }

    [Fact]
    public async Task GetAllTournamentsAsync_OverflowingOffset_ObservesCancellation()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateTournamentService(dbContext, [], sponsorPlacement: null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetAllTournamentsAsync(int.MaxValue, 50, cancellation.Token));
    }

    [Fact]
    public async Task StartTournamentAsync_LoadsPersistedRegistrationsBeforeCheckingParticipantCount()
    {
        await using var dbContext = CreateDbContext();
        var firstUser = CreateUser("start-first");
        var secondUser = CreateUser("start-second");
        var tournament = CreateTournament("Persisted registrations");
        tournament.TournamentRegistrations.Add(CreateActiveIndividualRegistration(tournament, firstUser));
        tournament.TournamentRegistrations.Add(CreateActiveIndividualRegistration(tournament, secondUser));
        dbContext.Users.AddRange(firstUser, secondUser);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateTournamentService(dbContext, [firstUser, secondUser], sponsorPlacement: null);

        await service.StartTournamentAsync(tournament.Id);

        Assert.Equal(TournamentStatus.InProgress, await dbContext.Set<TournamentAggregate>()
            .Where(candidate => candidate.Id == tournament.Id)
            .Select(candidate => candidate.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task UpdateTournamentAsync_RejectsParticipationModeChangeWhenPersistedRegistrationsExist()
    {
        await using var dbContext = CreateDbContext();
        var user = CreateUser("update-player");
        var tournament = CreateTournament("Protected tournament configuration");
        tournament.TournamentRegistrations.Add(CreateActiveIndividualRegistration(tournament, user));
        dbContext.Users.Add(user);
        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = CreateTournamentService(dbContext, [user], sponsorPlacement: null);
        var update = new UpdateTournamentDTO
        {
            Name = tournament.Name,
            BracketType = Mercurius.Modules.Tournament.Contracts.BracketType.SingleElimination,
            Format = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf1,
            FinalsFormat = Mercurius.Modules.Tournament.Contracts.GameFormat.BestOf3,
            ParticipationMode = Mercurius.Modules.Tournament.Contracts.ParticipationMode.Team,
            TeamSize = 2,
            PlannedStartTime = tournament.PlannedStartTime,
            AverageGameDurationMinutes = tournament.AverageGameDurationMinutes,
            RoundBreakDurationMinutes = tournament.RoundBreakDurationMinutes
        };

        await Assert.ThrowsAsync<ValidationException>(() => service.UpdateTournamentAsync(tournament.Id, update));
    }

    [Fact]
    public void TournamentService_ListQuery_StaysLean()
    {
        using var dbContext = CreateTranslationDbContext();
        var service = CreateTournamentService(dbContext, [], sponsorPlacement: null);

        var listQuery = (IQueryable)GetNonPublicMethod(typeof(TournamentService), "CreateTournamentListQuery")
            .Invoke(service, [])!;

        var listSql = listQuery.ToQueryString();

        Assert.DoesNotContain("TournamentRegistrations", listSql, StringComparison.Ordinal);
        Assert.DoesNotContain("RosterMembers", listSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Matches", listSql, StringComparison.Ordinal);
        Assert.DoesNotContain("Placements", listSql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", listSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchService_PublicRead_DoesNotMaterializeAnEntireBracket()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateTournament("Large match read");
        var targetMatch = new Match
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Tournament = tournament,
            Format = GameFormat.BestOf1,
            ParticipationMode = ParticipationMode.Individual,
            RoundNumber = 1,
            MatchNumber = 1
        };
        tournament.Matches.Add(targetMatch);
        for (var matchNumber = 2; matchNumber <= 101; matchNumber++)
        {
            tournament.Matches.Add(new Match
            {
                Id = Guid.NewGuid(),
                TournamentId = tournament.Id,
                Format = GameFormat.BestOf1,
                ParticipationMode = ParticipationMode.Individual,
                RoundNumber = matchNumber,
                MatchNumber = 1
            });
        }

        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            TimeProvider.System,
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));

        var result = await service.GetMatchByIdAsync(targetMatch.Id);

        Assert.Equal(targetMatch.Id, result.Id);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public void MatchService_PublicReadQuery_DoesNotJoinSiblingMatches()
    {
        using var dbContext = CreateTranslationDbContext();
        var service = new MatchService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            TournamentTestSupport.CreateIdentityModule(),
            TournamentTestSupport.CreateTeamsModule(),
            TournamentTestSupport.CreateModuleEventPublisher(),
            TimeProvider.System,
            new MatchBracketImpactAnalyzer(new TournamentDbContextAdapter<MercuriusDBContext>(dbContext)));
        var query = (IQueryable)GetNonPublicMethod(typeof(MatchService), "CreateMatchReadQuery")
            .Invoke(service, [Guid.NewGuid()])!;

        var sql = query.ToQueryString();

        Assert.DoesNotMatch(
            "JOIN\\s+(?:\"?tournament\"?\\.)?\"?matches\"?",
            sql);
    }

    [Fact]
    public void MatchDeadlineProcessor_QueryTargetsOnlyExpiredDeadlineCandidates()
    {
        using var dbContext = CreateTranslationDbContext();
        var queryMethod = typeof(MatchDeadlineProcessor).GetMethod(
            "CreateExpiredDeadlineQuery",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing deadline candidate query.");
        var query = (IQueryable<Match>)queryMethod.Invoke(
            null,
            [
                new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
                new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc)
            ])!;

        var sql = query.ToQueryString();

        Assert.Contains("ScoreConfirmationDeadlineUtc", sql, StringComparison.Ordinal);
        Assert.Contains("CorrectionDeadlineUtc", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            "JOIN\\s+(?:\"?tournament\"?\\.)?\"?matches\"?",
            sql);
    }

    [Fact]
    public async Task RegistrationMappingContextBuilder_LoadsUsersBeforeTeams()
    {
        var identityModule = new SequencedIdentityModule();
        var teamsModule = new SequencedTeamsModule();
        var builder = new RegistrationMappingContextBuilder(identityModule, teamsModule);
        var registration = new TournamentRegistration
        {
            UserId = Guid.NewGuid(),
            TeamId = Guid.NewGuid()
        };

        var buildTask = builder.BuildAsync([registration], [], CancellationToken.None);

        Assert.Equal(1, identityModule.CallCount);
        Assert.Equal(0, teamsModule.CallCount);

        identityModule.Complete();
        await teamsModule.WaitForCallAsync();
        teamsModule.Complete();

        await buildTask;
    }

    [Fact]
    public async Task TournamentDtoMapper_LoadsRegistrationContextBeforeSponsorPlacements()
    {
        var identityModule = new SequencedIdentityModule();
        var teamsModule = new SequencedTeamsModule();
        var sponsorshipModule = new SequencedSponsorshipModule();
        var mapper = new TournamentDtoMapper(
            new RegistrationMappingContextBuilder(identityModule, teamsModule),
            sponsorshipModule);
        var userId = Guid.NewGuid();
        var tournament = CreateTournament("Sequenced mapper");
        tournament.TournamentRegistrations.Add(new TournamentRegistration
        {
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = userId,
            RegisteredByUsernameAtRegistration = "alpha",
            UserId = userId,
            UsernameAtRegistration = "alpha"
        });

        var mappingTask = mapper.ToGetTournamentDtosAsync([tournament], CancellationToken.None);

        Assert.Equal(1, identityModule.CallCount);
        Assert.Equal(0, sponsorshipModule.CallCount);

        identityModule.Complete();
        await teamsModule.WaitForCallAsync();
        teamsModule.Complete();
        await sponsorshipModule.WaitForCallAsync();
        sponsorshipModule.Complete();

        await mappingTask;
    }

    [Fact]
    public async Task GetCurrentUserStateAsync_OnlyHydratesRegistrationsForCurrentCaptainedTeams()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateTournament("Bounded captain lookup");
        var currentCaptainId = Guid.NewGuid();
        var formerCaptainId = Guid.NewGuid();
        var captainedTeamId = new TeamId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var captainedRegistration = CreateTeamRegistration(
            tournament,
            captainedTeamId.Value,
            formerCaptainId,
            formerCaptainId);
        tournament.TournamentRegistrations.Add(captainedRegistration);

        var unrelatedTeamIds = Enumerable.Range(2, 25)
            .Select(value => Guid.Parse($"00000000-0000-0000-0000-{value:D12}"))
            .ToArray();
        foreach (var teamId in unrelatedTeamIds)
        {
            var unrelatedUserId = Guid.NewGuid();
            tournament.TournamentRegistrations.Add(CreateTeamRegistration(
                tournament,
                teamId,
                unrelatedUserId,
                unrelatedUserId));
        }

        dbContext.Set<TournamentAggregate>().Add(tournament);
        await dbContext.SaveChangesAsync();

        var teamsModule = new TrackingCurrentCaptainTeamsModule(
            new UserId(currentCaptainId),
            new TeamRosterSnapshot(
                captainedTeamId,
                "Current Captain Team",
                new UserId(currentCaptainId),
                null,
                false,
                []));
        var identityModule = TournamentTestSupport.CreateIdentityModule();
        var contextBuilder = new RegistrationMappingContextBuilder(identityModule, teamsModule);
        var readModelService = new TournamentRegistrationReadModelService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            teamsModule,
            contextBuilder,
            new TournamentDtoMapper(contextBuilder, new StaticSponsorshipModule(null)));

        var state = await readModelService.GetCurrentUserStateAsync(
            currentCaptainId,
            tournament,
            CancellationToken.None);

        var captainManaged = Assert.Single(state.CaptainManagedRegistrations);
        Assert.Equal(captainedRegistration.Id, captainManaged.Id);
        Assert.Equal(currentCaptainId, captainManaged.Team?.CaptainUserId);
        Assert.True(state.CanUnregister);
        Assert.Equal(1, teamsModule.CaptainedTeamIdsCallCount);
        var rosterBatch = Assert.Single(teamsModule.RosterBatchTeamIds);
        Assert.Equal([captainedTeamId.Value], rosterBatch.Select(teamId => teamId.Value).ToArray());
        Assert.DoesNotContain(rosterBatch, teamId => unrelatedTeamIds.Contains(teamId.Value));
    }

    [Fact]
    public void SearchTournamentsAsync_UsesLowerNamePredicate_ForFunctionalTrigramIndexAlignment()
    {
        using var dbContext = CreateTranslationDbContext();
        var tournamentDbContext = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        var identityModule = TournamentTestSupport.CreateIdentityModule();
        var teamsModule = TournamentTestSupport.CreateTeamsModule();
        var facade = new TournamentModuleFacade(
            tournamentDbContext,
            teamsModule,
            new TournamentEligibilityEvaluator(tournamentDbContext),
            new PublicProfileMatchSummaryReadService(tournamentDbContext, identityModule, teamsModule));

        var buildQuery = GetNonPublicMethod(typeof(TournamentModuleFacade), "BuildTournamentSearchQuery");
        var query = (IQueryable)buildQuery.Invoke(facade, ["alpha"])!;
        var sql = query.ToQueryString();

        Assert.Contains("lower(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Name\"", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ILIKE", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static MethodInfo GetNonPublicMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing method {name}.");

    private static TournamentService CreateTournamentService(
        MercuriusDBContext dbContext,
        IReadOnlyCollection<User> users,
        SponsorPlacementSummary? sponsorPlacement,
        ISponsorshipModule? sponsorshipModule = null)
    {
        return new TournamentService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext),
            new FixedMatchModeratorFactory(),
            new StubMediaModule(),
            TournamentTestSupport.CreateIdentityModule(users),
            sponsorshipModule ?? new StaticSponsorshipModule(sponsorPlacement),
            TournamentTestSupport.CreateMapper(
                users,
                sponsorPlacement: sponsorPlacement,
                sponsorshipModule: sponsorshipModule),
            TournamentTestSupport.CreateModuleEventPublisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TournamentService>.Instance);
    }

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

    private static TournamentAggregate CreateTournament(string name) => new(
        name,
        BracketType.SingleElimination,
        GameFormat.BestOf1,
        GameFormat.BestOf3,
        ParticipationMode.Individual,
        null,
        DateTime.UtcNow,
        30,
        10);

    private static TournamentRegistration CreateActiveIndividualRegistration(TournamentAggregate tournament, User user) => new()
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
    };

    private static TournamentRegistration CreateTeamRegistration(
        TournamentAggregate tournament,
        Guid teamId,
        Guid captainUserIdAtRegistration,
        Guid rosterUserId)
    {
        var registration = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = captainUserIdAtRegistration,
            RegisteredByUsernameAtRegistration = "snapshot-captain",
            TeamId = teamId,
            TeamNameAtRegistration = "Snapshot Team",
            TeamCaptainUserIdAtRegistration = captainUserIdAtRegistration
        };
        registration.RosterMembers.Add(new TournamentRegistrationRosterMember
        {
            Id = Guid.NewGuid(),
            TournamentRegistration = registration,
            TournamentRegistrationId = registration.Id,
            Tournament = tournament,
            TournamentId = tournament.Id,
            TeamId = teamId,
            TeamNameAtRegistration = registration.TeamNameAtRegistration,
            UserId = rosterUserId,
            UsernameAtRegistration = "snapshot-player",
            DisplayNameAtRegistration = "Snapshot Player",
            IsCaptain = rosterUserId == captainUserIdAtRegistration,
            ConfirmationStatus = RosterMemberConfirmationStatus.Confirmed
        });
        return registration;
    }

    private static User CreateUser(string username)
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Auth0UserId = $"auth0|{username}",
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            Firstname = username,
            Lastname = "Player",
            Email = $"{username}@example.com"
        };
    }

    private static SponsorPlacementSummary CreateSponsorPlacement(Guid tournamentId)
    {
        return new SponsorPlacementSummary(
            new SponsorPlacementId(1),
            new TournamentId(tournamentId),
            new SponsorSummary(
                new SponsorId(1),
                "Sponsor",
                SponsorTier.Gold,
                "logo.png",
                "https://example.com",
                "desc"),
            SponsorContext.TournamentPartner,
            "Headline",
            "Support",
            1);
    }

    private sealed class FixedMatchModeratorFactory : IMatchModeratorFactory
    {
        public IMatchModerator GetMatchModerator(BracketType bracketType) => new NoOpMatchModerator();
    }

    private sealed class NoOpMatchModerator : IMatchModerator
    {
        public IEnumerable<Match> GenerateMatchesForTournament(TournamentAggregate tournament) => [];

        public void EnsureCanComplete(TournamentAggregate tournament)
        {
        }

        public void DeterminePlacements(TournamentAggregate tournament)
        {
        }
    }

    private sealed class StubMediaModule : IMediaModule
    {
        public Task<StoredMediaAsset> SaveImageAsync(MediaUpload upload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StoredMediaAsset("image.png"));

        public Task DeleteImageAsync(string? imageUrl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StaticSponsorshipModule(SponsorPlacementSummary? sponsorPlacement) : ISponsorshipModule
    {
        public int BatchCallCount { get; private set; }
        public IReadOnlyCollection<TournamentId> LastBatchTournamentIds { get; private set; } = [];

        public Task<SponsorSummary?> GetSponsorSummaryAsync(SponsorId sponsorId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SponsorSummary?>(null);

        public Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(
            SponsorId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SponsorSearchDocument>>([]);

        public Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(
            IReadOnlyCollection<TournamentId> tournamentIds,
            CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            LastBatchTournamentIds = tournamentIds;
            if (sponsorPlacement is null)
                return Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(new Dictionary<TournamentId, SponsorPlacementSummary>());

            return Task.FromResult<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>>(
                new Dictionary<TournamentId, SponsorPlacementSummary> { [sponsorPlacement.TournamentId] = sponsorPlacement });
        }

        public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(sponsorPlacement?.TournamentId == tournamentId ? sponsorPlacement : null);

        public Task ReplaceSponsorPlacementAsync(TournamentId tournamentId, SponsorPlacementInput? placement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SequencedIdentityModule : IIdentityModule
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public void Complete() => _completion.TrySetResult();

        public async Task<IReadOnlyDictionary<UserId, UserProfileSummary>> GetUsersByIdsAsync(
            IReadOnlyCollection<UserId> userIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await _completion.Task.WaitAsync(cancellationToken);
            return new Dictionary<UserId, UserProfileSummary>();
        }

        public Task<IReadOnlyDictionary<UserId, string>> GetPublicUsernamesByIdsAsync(
            IReadOnlyCollection<UserId> userIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<UserId, string>>(new Dictionary<UserId, string>());

        public Task<UserProfileSummary?> GetUserProfileAsync(UserId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserProfileSummary?>(null);

        public Task<UserProfileSummary?> GetUserProfileByAuth0IdAsync(string auth0UserId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UserProfileSummary?>(null);

        public Task<PublicUserProfileSummary?> GetPublicProfileByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicUserProfileSummary?>(null);

        public Task<PublicUserProfileSummary?> GetPublicProfileByIdAsync(UserId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicUserProfileSummary?>(null);

        public Task<IReadOnlyList<PublicUserSearchDocument>> GetPublicUserSearchDocumentsPageAsync(
            UserId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicUserSearchDocument>>([]);
    }

    private sealed class SequencedTeamsModule : ITeamsModule
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task WaitForCallAsync() => _called.Task;

        public void Complete() => _completion.TrySetResult();

        public async Task<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>> GetTeamRosterSnapshotsAsync(
            IReadOnlyCollection<TeamId> teamIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _called.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            return new Dictionary<TeamId, TeamRosterSnapshot>();
        }

        public Task<IReadOnlyDictionary<TeamId, string>> GetPublicTeamNamesByIdsAsync(
            IReadOnlyCollection<TeamId> teamIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<TeamId, string>>(new Dictionary<TeamId, string>());

        public Task<TeamSummary?> GetTeamSummaryAsync(TeamId teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamSummary?>(null);

        public Task<IReadOnlyList<TeamId>> GetCaptainedTeamIdsAsync(
            UserId userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TeamId>>([]);

        public Task<TeamRosterSnapshot?> GetTeamRosterSnapshotAsync(TeamId teamId, CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamRosterSnapshot?>(null);

        public Task<PublicTeamProfile?> GetPublicTeamProfileAsync(string teamName, CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicTeamProfile?>(null);

        public Task<TeamId?> GetPublicTeamIdByNameAsync(string teamName, CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamId?>(null);

        public Task<TeamRegistrationEligibility> GetRegistrationEligibilityAsync(
            TeamId teamId,
            UserId requestedBy,
            TournamentId tournamentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamRegistrationEligibility(true, []));

        public Task<MembershipMutationGuard> CanMutateMembershipAsync(
            TeamId teamId,
            UserId userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MembershipMutationGuard(true, []));

        public Task<IReadOnlyList<PublicTeamSearchDocument>> GetPublicTeamSearchDocumentsPageAsync(
            TeamId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicTeamSearchDocument>>([]);
    }

    private sealed class TrackingCurrentCaptainTeamsModule(
        UserId currentCaptainId,
        TeamRosterSnapshot team) : ITeamsModule
    {
        public int CaptainedTeamIdsCallCount { get; private set; }
        public List<IReadOnlyCollection<TeamId>> RosterBatchTeamIds { get; } = [];

        public Task<IReadOnlyList<TeamId>> GetCaptainedTeamIdsAsync(
            UserId userId,
            CancellationToken cancellationToken = default)
        {
            CaptainedTeamIdsCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TeamId>>(
                userId == currentCaptainId ? [team.TeamId] : []);
        }

        public Task<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>> GetTeamRosterSnapshotsAsync(
            IReadOnlyCollection<TeamId> teamIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedTeamIds = teamIds.ToArray();
            RosterBatchTeamIds.Add(requestedTeamIds);
            return Task.FromResult<IReadOnlyDictionary<TeamId, TeamRosterSnapshot>>(
                requestedTeamIds.Contains(team.TeamId)
                    ? new Dictionary<TeamId, TeamRosterSnapshot> { [team.TeamId] = team }
                    : new Dictionary<TeamId, TeamRosterSnapshot>());
        }

        public Task<IReadOnlyDictionary<TeamId, string>> GetPublicTeamNamesByIdsAsync(
            IReadOnlyCollection<TeamId> teamIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<TeamId, string>>(new Dictionary<TeamId, string>());

        public Task<TeamSummary?> GetTeamSummaryAsync(
            TeamId teamId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamSummary?>(null);

        public Task<TeamRosterSnapshot?> GetTeamRosterSnapshotAsync(
            TeamId teamId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamRosterSnapshot?>(teamId == team.TeamId ? team : null);

        public Task<PublicTeamProfile?> GetPublicTeamProfileAsync(
            string teamName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<PublicTeamProfile?>(null);

        public Task<TeamId?> GetPublicTeamIdByNameAsync(
            string teamName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TeamId?>(null);

        public Task<TeamRegistrationEligibility> GetRegistrationEligibilityAsync(
            TeamId teamId,
            UserId requestedBy,
            TournamentId tournamentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TeamRegistrationEligibility(true, []));

        public Task<MembershipMutationGuard> CanMutateMembershipAsync(
            TeamId teamId,
            UserId userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MembershipMutationGuard(true, []));

        public Task<IReadOnlyList<PublicTeamSearchDocument>> GetPublicTeamSearchDocumentsPageAsync(
            TeamId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PublicTeamSearchDocument>>([]);
    }

    private sealed class SequencedSponsorshipModule : ISponsorshipModule
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task WaitForCallAsync() => _called.Task;

        public void Complete() => _completion.TrySetResult();

        public async Task<IReadOnlyDictionary<TournamentId, SponsorPlacementSummary>> GetSponsorPlacementsAsync(
            IReadOnlyCollection<TournamentId> tournamentIds,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            _called.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
            return new Dictionary<TournamentId, SponsorPlacementSummary>();
        }

        public Task<SponsorSummary?> GetSponsorSummaryAsync(SponsorId sponsorId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SponsorSummary?>(null);

        public Task<IReadOnlyList<SponsorSearchDocument>> GetSponsorSearchDocumentsPageAsync(
            SponsorId? afterId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SponsorSearchDocument>>([]);

        public Task<SponsorPlacementSummary?> GetSponsorPlacementAsync(TournamentId tournamentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SponsorPlacementSummary?>(null);

        public Task ReplaceSponsorPlacementAsync(TournamentId tournamentId, SponsorPlacementInput? placement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
