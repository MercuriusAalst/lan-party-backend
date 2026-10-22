using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Identity.Infrastructure;

internal sealed class Auth0SubjectFenceConfiguration : IEntityTypeConfiguration<Auth0SubjectFence>
{
    public void Configure(EntityTypeBuilder<Auth0SubjectFence> entity)
    {
        entity.ToTable("auth0_subject_fences", "identity");
        entity.HasKey(fence => fence.SubjectHash);
        entity.Property(fence => fence.SubjectHash).HasMaxLength(64).IsRequired();
        entity.Property(fence => fence.PendingCleanupJobId);
    }
}
