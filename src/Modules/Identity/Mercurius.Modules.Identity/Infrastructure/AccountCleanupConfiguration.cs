using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Identity.Infrastructure;

internal sealed class AccountCleanupConfiguration : IEntityTypeConfiguration<AccountCleanup>
{
    public void Configure(EntityTypeBuilder<AccountCleanup> entity)
    {
        entity.ToTable("account_cleanups", "identity");
        entity.HasKey(cleanup => cleanup.Id);
        entity.Property(cleanup => cleanup.SubjectHash).HasMaxLength(64).IsRequired();
        entity.Property(cleanup => cleanup.Subject).HasMaxLength(512).IsRequired();
        entity.Property(cleanup => cleanup.SanitizedOutcome).HasMaxLength(200);
        entity.HasIndex(cleanup => new { cleanup.UserId, cleanup.RequestedAtUtc });
        entity.HasIndex(cleanup => cleanup.SubjectHash);
        entity.HasIndex(cleanup => cleanup.OutboxMessageId).IsUnique();
    }
}
