using Mercurius.Modules.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mercurius.Modules.Teams.Infrastructure;

internal sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> entity)
    {
        entity.ToTable("team_members", "teams");
        entity.HasKey(member => new { member.TeamId, member.UserId })
            .HasName("PK_team_members");
        entity.Property(member => member.JoinedAtUtc);
        entity.HasIndex(member => member.UserId)
            .HasDatabaseName("IX_team_members_UserId");
        entity.HasOne(member => member.Team)
            .WithMany(team => team.Members)
            .HasForeignKey(member => member.TeamId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_team_members_teams_TeamId");
    }
}
