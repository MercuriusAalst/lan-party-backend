using Mercurius.Modules.Sponsorship.Contracts;
using Mercurius.Modules.Sponsorship.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Mercurius.Modules.Sponsorship.Infrastructure;

internal sealed class SponsorConfiguration : IEntityTypeConfiguration<Sponsor>
{
    public void Configure(EntityTypeBuilder<Sponsor> entity)
    {
        var sponsorTierConverter = new EnumToStringConverter<SponsorTier>();

        entity.ToTable("sponsors", "sponsorship");
        entity.HasKey(sponsor => sponsor.Id);
        entity.Property(sponsor => sponsor.Name).IsRequired();
        entity.Property(sponsor => sponsor.LogoUrl).IsRequired();
        entity.Property(sponsor => sponsor.LightLogoUrl);
        entity.Property(sponsor => sponsor.InfoUrl).IsRequired();
        entity.Property(sponsor => sponsor.Description).HasMaxLength(1200);
        entity.Property(sponsor => sponsor.SponsorTier)
            .HasConversion(sponsorTierConverter)
            .IsRequired();
        entity.HasMany(sponsor => sponsor.TournamentSponsorPlacements)
            .WithOne(placement => placement.Sponsor)
            .HasForeignKey(placement => placement.SponsorId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
