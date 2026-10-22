using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Identity.Infrastructure;

internal sealed class Auth0AccountCleanupConfiguration : IEntityTypeConfiguration<Auth0AccountCleanup>
{
    public void Configure(EntityTypeBuilder<Auth0AccountCleanup> entity)
    {
        entity.ToTable("auth0_account_cleanups", "identity");
        entity.HasKey(cleanup => cleanup.Id);
        entity.Property(cleanup => cleanup.SubjectHash).HasMaxLength(64).IsRequired();
        entity.Property(cleanup => cleanup.EncryptedSubject).HasMaxLength(4096).IsRequired();
        entity.Property(cleanup => cleanup.SanitizedOutcome).HasMaxLength(200);
        entity.HasIndex(cleanup => new { cleanup.UserId, cleanup.RequestedAtUtc });
        entity.HasIndex(cleanup => cleanup.SubjectHash);
        entity.HasIndex(cleanup => cleanup.OutboxMessageId).IsUnique();
    }
}
