using Mercurius.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Identity.Infrastructure;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("users", "identity");
        entity.HasIndex(user => user.Auth0UserId).IsUnique();
        entity.HasIndex(user => user.Username)
            .IsUnique()
            .HasFilter("\"Username\" IS NOT NULL AND \"IsDeleted\" = false");
        entity.HasIndex(user => user.NormalizedUsername)
            .IsUnique()
            .HasFilter("\"NormalizedUsername\" IS NOT NULL AND \"IsDeleted\" = false");
        entity.HasIndex(user => user.Email)
            .IsUnique()
            .HasFilter("\"Email\" IS NOT NULL AND \"IsDeleted\" = false");
        entity.Property(user => user.Auth0UserId).IsRequired().HasMaxLength(200);
        entity.Property(user => user.Username).HasMaxLength(32);
        entity.Property(user => user.NormalizedUsername).HasMaxLength(32);
        entity.Property(user => user.Firstname).HasMaxLength(100);
        entity.Property(user => user.Lastname).HasMaxLength(100);
        entity.Property(user => user.Email).HasMaxLength(254);
        entity.Property(user => user.DiscordId).HasMaxLength(100);
        entity.Property(user => user.SteamId).HasMaxLength(100);
        entity.Property(user => user.RiotId).HasMaxLength(100);
        entity.Property(user => user.ProfilePictureUrl).HasMaxLength(2048);
        entity.Property(user => user.CreatedAtUtc).IsRequired();
        entity.Property(user => user.UpdatedAtUtc).IsRequired();
    }
}
