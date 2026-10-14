using Mercurius.Modules.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Teams.Infrastructure;

internal sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> entity)
    {
        entity.ToTable("teams", "teams");
        entity.Property(team => team.Name).IsRequired().HasMaxLength(100);
        entity.Property(team => team.NormalizedName).IsRequired().HasMaxLength(100);
        entity.Property(team => team.LogoUrl).HasMaxLength(260);
        entity.Property(team => team.IsDeleted).IsRequired();
        entity.Property(team => team.Version).IsRequired().IsConcurrencyToken();
        entity.HasIndex(team => team.NormalizedName)
            .IsUnique()
            .HasFilter("\"IsDeleted\" = false");
        entity.HasIndex(team => team.CaptainUserId);
    }
}
