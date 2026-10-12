using Mercurius.Modules.Tournament.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Tournament.Infrastructure;

internal sealed class FeaturedHomepageTournamentSelectionConfiguration : IEntityTypeConfiguration<FeaturedHomepageTournamentSelection>
{
    public void Configure(EntityTypeBuilder<FeaturedHomepageTournamentSelection> entity)
    {
        entity.ToTable("featured_homepage_tournaments", "tournament", table =>
        {
            table.HasCheckConstraint("CK_featured_homepage_tournaments_singleton", "\"Id\" = 1");
            table.HasCheckConstraint("CK_featured_homepage_tournaments_count", "cardinality(\"TournamentIds\") BETWEEN 1 AND 4");
        });
        entity.HasKey(selection => selection.Id);
        entity.Property(selection => selection.TournamentIds)
            .HasColumnType("uuid[]")
            .IsRequired();
    }
}
