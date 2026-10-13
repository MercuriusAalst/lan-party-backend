using Mercurius.Modules.Sponsorship.Infrastructure;
using Platform.Eventing;

namespace Mercurius.Modules.Sponsorship.Application;

internal sealed class SponsorshipOutboxWriter
{
    private readonly ISponsorshipDbContext _dbContext;
    private readonly IModuleEventPublisher _moduleEventPublisher;

    public SponsorshipOutboxWriter(
        ISponsorshipDbContext dbContext,
        IModuleEventPublisher moduleEventPublisher)
    {
        _dbContext = dbContext;
        _moduleEventPublisher = moduleEventPublisher;
    }

    public async Task SaveAndPublishAsync<TPayload>(
        Func<TPayload> createPayload,
        CancellationToken cancellationToken = default)
        where TPayload : notnull
    {
        await using var transaction = _dbContext.Database.CurrentTransaction is null
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _moduleEventPublisher.Publish(createPayload());
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }
}
