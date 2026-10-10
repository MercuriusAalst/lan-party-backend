using Mercurius.Modules.Tournament;
using Mercurius.Modules.Discovery;
using Mercurius.Modules.Identity;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Sponsorship;
using Mercurius.Modules.Teams;
using Mercurius.Modules.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Platform.Eventing.Persistence;

namespace Mercurius.LAN.API.Data;

public class MercuriusDBContext : DbContext, IModuleEventDbContext
{
    private const string TeamInviteEntityType = "Mercurius.Modules.Teams.Domain.TeamInvite";
    private const string TeamMemberEntityType = "Mercurius.Modules.Teams.Domain.TeamMember";
    private const string MatchEntityType = "Mercurius.Modules.Tournament.Domain.Match";
    private const string TournamentRegistrationEntityType = "Mercurius.Modules.Tournament.Domain.TournamentRegistration";
    private const string TournamentRegistrationRosterMemberEntityType = "Mercurius.Modules.Tournament.Domain.TournamentRegistrationRosterMember";
    private const string PlacementUserEntityType = "Mercurius.Modules.Tournament.Domain.PlacementUser";
    private const string PlacementTeamEntityType = "Mercurius.Modules.Tournament.Domain.PlacementTeam";
    private const string TournamentEntityType = "Mercurius.Modules.Tournament.Domain.Tournament";
    private const string TournamentSponsorPlacementEntityType = "Mercurius.Modules.Sponsorship.Domain.TournamentSponsorPlacement";

    public MercuriusDBContext()
    {
    }

    public MercuriusDBContext(DbContextOptions<MercuriusDBContext> options) : base(options)
    {
    }

    public DbSet<Team> Teams { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<InboxMessage> InboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyIdentityModelConfiguration();
        modelBuilder.ApplyTeamsModelConfiguration();
        modelBuilder.ApplyTournamentModelConfiguration();
        modelBuilder.ApplySponsorshipModelConfiguration();
        modelBuilder.ApplyDiscoveryModelConfiguration();
        modelBuilder.ApplyEventingModelConfiguration();
        ConfigureCrossModuleRelationships(modelBuilder);
    }

    private static void ConfigureCrossModuleRelationships(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>()
            .HasOne(typeof(User).FullName!, null)
            .WithMany()
            .HasForeignKey(nameof(Team.CaptainUserId))
            .IsRequired(false)
            .OnDelete(DeleteBehavior.ClientSetNull)
            .HasConstraintName("FK_teams_users_CaptainUserId");

        modelBuilder.Entity(TeamMemberEntityType)
            .HasOne(typeof(User).FullName!, null)
            .WithMany()
            .HasForeignKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_team_members_users_UserId");

        modelBuilder.Entity(TeamInviteEntityType)
            .HasOne(typeof(User).FullName!, null)
            .WithMany()
            .HasForeignKey("UserId")
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_team_invites_users_UserId");

        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "UserParticipant1Id");
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "UserParticipant2Id");
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "UserWinnerId");
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "UserLoserId");
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "TeamParticipant1Id", typeof(Team).FullName!);
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "TeamParticipant2Id", typeof(Team).FullName!);
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "TeamWinnerId", typeof(Team).FullName!);
        ConfigureOptionalCrossModuleReference(modelBuilder, MatchEntityType, "TeamLoserId", typeof(Team).FullName!);

        ConfigureRequiredCrossModuleReference(modelBuilder, TournamentRegistrationEntityType, "RegisteredByUserId", DeleteBehavior.Restrict);
        ConfigureOptionalCrossModuleReference(modelBuilder, TournamentRegistrationEntityType, "UserId", typeof(User).FullName!, DeleteBehavior.Restrict);
        ConfigureOptionalCrossModuleReference(modelBuilder, TournamentRegistrationEntityType, "TeamId", typeof(Team).FullName!, DeleteBehavior.Restrict);
        ConfigureRequiredCrossModuleReference(modelBuilder, TournamentRegistrationRosterMemberEntityType, "UserId", DeleteBehavior.Restrict);
        ConfigureOptionalCrossModuleReference(modelBuilder, TournamentRegistrationRosterMemberEntityType, "TeamId", typeof(Team).FullName!, DeleteBehavior.Restrict);
        ConfigureRequiredCrossModuleReference(modelBuilder, PlacementUserEntityType, "UserId", DeleteBehavior.Restrict);
        ConfigureRequiredCrossModuleReference(modelBuilder, PlacementTeamEntityType, "TeamId", DeleteBehavior.Restrict, typeof(Team).FullName!);

        modelBuilder.Entity(TournamentEntityType)
            .HasOne(TournamentSponsorPlacementEntityType, null)
            .WithOne()
            .HasForeignKey(TournamentSponsorPlacementEntityType, "TournamentId")
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOptionalCrossModuleReference(
        ModelBuilder modelBuilder,
        string dependentEntityType,
        string foreignKeyProperty,
        string? principalEntityType = null,
        DeleteBehavior deleteBehavior = DeleteBehavior.NoAction)
    {
        modelBuilder.Entity(dependentEntityType)
            .HasOne(principalEntityType ?? typeof(User).FullName!, null)
            .WithMany()
            .HasForeignKey(foreignKeyProperty)
            .IsRequired(false)
            .OnDelete(deleteBehavior);
    }

    private static void ConfigureRequiredCrossModuleReference(
        ModelBuilder modelBuilder,
        string dependentEntityType,
        string foreignKeyProperty,
        DeleteBehavior deleteBehavior,
        string? principalEntityType = null)
    {
        modelBuilder.Entity(dependentEntityType)
            .HasOne(principalEntityType ?? typeof(User).FullName!, null)
            .WithMany()
            .HasForeignKey(foreignKeyProperty)
            .IsRequired()
            .OnDelete(deleteBehavior);
    }
}
