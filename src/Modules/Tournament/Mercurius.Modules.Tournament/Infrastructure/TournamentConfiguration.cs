using Mercurius.Modules.Tournament.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Tournament.Infrastructure;

internal sealed class TournamentConfiguration : IEntityTypeConfiguration<TournamentAggregate>
{
    public void Configure(EntityTypeBuilder<TournamentAggregate> entity)
    {
        entity.ToTable("tournaments", "tournament");
        entity.HasKey(tournament => tournament.Id);
        entity.Property(tournament => tournament.Name).IsRequired();
        entity.Property(tournament => tournament.StartTime).IsRequired();
        entity.Property(tournament => tournament.EndTime).IsRequired();
        entity.Property(tournament => tournament.PlannedStartTime).IsRequired();
        entity.Property(tournament => tournament.AverageGameDurationMinutes).IsRequired();
        entity.Property(tournament => tournament.RoundBreakDurationMinutes).IsRequired();
        entity.Property(tournament => tournament.EstimatedEndTime).IsRequired(false);
        entity.Property(tournament => tournament.TeamSize).IsRequired(false);
        entity.Property(tournament => tournament.FirstPlacePrize).HasMaxLength(200).IsRequired(false);
        entity.Property(tournament => tournament.SecondPlacePrize).HasMaxLength(200).IsRequired(false);
        entity.Property(tournament => tournament.ThirdPlacePrize).HasMaxLength(200).IsRequired(false);
        entity.Property(tournament => tournament.Status).IsConcurrencyToken();
        entity.Property(tournament => tournament.LeaderboardRevision).IsConcurrencyToken();
    }
}
