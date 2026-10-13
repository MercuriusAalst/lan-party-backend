using Mercurius.TestInfrastructure;
using Mercurius.LAN.API.Data;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Contracts;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Platform.Eventing;
using DomainRecipientKind = Mercurius.Modules.Tournament.Domain.MatchResolutionNotificationRecipientKind;

namespace Mercurius.Modules.Tournament.Tests;

public class MatchResolutionNotificationTests
{
    [Fact]
    public async Task Handler_PersistsAssignedAdminRecipient_AndIsIdempotent()
    {
        await using var dbContext = CreateDbContext();
        var tournamentDbContext = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        var handler = new MatchResolutionNotificationHandler(tournamentDbContext);
        var messageId = Guid.NewGuid();
        var matchId = Guid.NewGuid();
        var tournamentId = Guid.NewGuid();
        var adminUserId = Guid.NewGuid();
        var occurredAtUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var payload = new MatchResolutionRequiredIntegrationEvent(
            new MatchId(matchId),
            new TournamentId(tournamentId),
            adminUserId);
        var context = new ModuleEventContext(
            messageId,
            typeof(MatchResolutionRequiredIntegrationEvent).FullName!,
            occurredAtUtc);

        await handler.HandleAsync(payload, context);
        await handler.HandleAsync(
            payload with { AssignedAdminUserId = Guid.NewGuid() },
            context);
        await dbContext.SaveChangesAsync();

        await handler.HandleAsync(
            payload with { AssignedAdminUserId = Guid.NewGuid() },
            context);
        await dbContext.SaveChangesAsync();

        var notification = await tournamentDbContext.MatchResolutionNotifications.SingleAsync();
        Assert.Equal(messageId, notification.Id);
        Assert.Equal(matchId, notification.MatchId);
        Assert.Equal(tournamentId, notification.TournamentId);
        Assert.Equal(adminUserId, notification.RecipientUserId);
        Assert.Equal(
            DomainRecipientKind.AssignedAdmin,
            notification.RecipientKind);
        Assert.Equal(occurredAtUtc, notification.OccurredAtUtc);
        Assert.Equal(occurredAtUtc, notification.CreatedAtUtc);
    }

    [Fact]
    public async Task Handler_PersistsGlobalAdminFallbackWhenNoAssignmentExists()
    {
        await using var dbContext = CreateDbContext();
        var tournamentDbContext = new TournamentDbContextAdapter<MercuriusDBContext>(dbContext);
        var handler = new MatchResolutionNotificationHandler(tournamentDbContext);
        var occurredAtUtc = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var payload = new MatchResolutionRequiredIntegrationEvent(
            new MatchId(Guid.NewGuid()),
            new TournamentId(Guid.NewGuid()));
        var context = new ModuleEventContext(
            Guid.NewGuid(),
            typeof(MatchResolutionRequiredIntegrationEvent).FullName!,
            occurredAtUtc);

        await handler.HandleAsync(payload, context);
        await dbContext.SaveChangesAsync();

        var notification = await tournamentDbContext.MatchResolutionNotifications.SingleAsync();
        Assert.Null(notification.RecipientUserId);
        Assert.Equal(
            DomainRecipientKind.GlobalAdmin,
            notification.RecipientKind);
    }

    private static MercuriusDBContext CreateDbContext() => PostgresTestDatabase.CreateDbContext();
}
