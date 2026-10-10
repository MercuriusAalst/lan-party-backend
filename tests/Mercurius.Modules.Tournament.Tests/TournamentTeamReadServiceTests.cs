using Mercurius.TestInfrastructure;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mercurius.Modules.Tournament.Tests;

public sealed class TournamentTeamReadServiceTests
{
    [Fact]
    public async Task IsTeamLogoReferencedAsync_UsesExactHistoricalSnapshotMatchWithoutTrackingEntities()
    {
        await using var dbContext = CreateDbContext();
        var tournament = CreateTournament();
        dbContext.Set<TournamentAggregate>().Add(tournament);
        dbContext.Set<TournamentRegistration>().AddRange(
            CreateTeamRegistration(tournament, "images/Team-Logo.webp"),
            CreateTeamRegistration(tournament, "images/another-logo.webp"));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var service = new TournamentTeamReadService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext));

        var exactMatch = await service.IsTeamLogoReferencedAsync("images/Team-Logo.webp");
        var casingMismatch = await service.IsTeamLogoReferencedAsync("images/team-logo.webp");
        var absent = await service.IsTeamLogoReferencedAsync("images/missing-logo.webp");

        Assert.True(exactMatch);
        Assert.False(casingMismatch);
        Assert.False(absent);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task IsTeamLogoReferencedAsync_WhenCanceled_PropagatesCancellation()
    {
        await using var dbContext = CreateDbContext();
        var service = new TournamentTeamReadService(
            new TournamentDbContextAdapter<MercuriusDBContext>(dbContext));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.IsTeamLogoReferencedAsync("images/team-logo.webp", cancellationSource.Token));
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new MercuriusDBContext(options);
    }

    private static TournamentAggregate CreateTournament()
    {
        return new TournamentAggregate(
            "Historical Logo Cup",
            BracketType.SingleElimination,
            GameFormat.BestOf1,
            GameFormat.BestOf3,
            ParticipationMode.Team,
            5).Set(x => x.Id, Guid.NewGuid());
    }

    private static TournamentRegistration CreateTeamRegistration(TournamentAggregate tournament, string logoUrl)
    {
        return new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            Tournament = tournament,
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = Guid.NewGuid(),
            RegisteredByUsernameAtRegistration = "captain",
            TeamId = Guid.NewGuid(),
            TeamNameAtRegistration = "Historical Team",
            TeamLogoUrlAtRegistration = logoUrl
        };
    }
}
