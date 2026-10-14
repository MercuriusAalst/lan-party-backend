using System.Reflection;
using System.Text.RegularExpressions;
using Mercurius.LAN.API.Data;
using Mercurius.LAN.API.Migrations;
using Mercurius.Modules.Identity.Domain;
using Mercurius.Modules.Teams.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Platform.Tests;

public class PersistenceBoundaryTests
{
    [Fact]
    public void ModuleSchemaOwnership_IsDiscoveredByEfCore()
    {
        using var dbContext = CreateDbContext();
        var migrations = dbContext.GetService<IMigrationsAssembly>();

        Assert.Contains("20260807143500_ModuleSchemaOwnership", migrations.Migrations.Keys);
    }

    [Fact]
    public void SynchronizeModularModelSnapshot_IsDiscoveredByEfCore()
    {
        using var dbContext = CreateDbContext();
        var migrations = dbContext.GetService<IMigrationsAssembly>();

        Assert.Contains("20260809152209_SynchronizeModularModelSnapshot", migrations.Migrations.Keys);
    }

    [Fact]
    public void SynchronizeModularModelSnapshot_OnlyAlignsCatalogMetadataInBothDirections()
    {
        var migration = new SynchronizeModularModelSnapshot();
        var upOperation = Assert.Single(migration.UpOperations.OfType<SqlOperation>());
        var downOperation = Assert.Single(migration.DownOperations.OfType<SqlOperation>());

        Assert.Equal(45, Regex.Count(upOperation.Sql, "RENAME CONSTRAINT", RegexOptions.CultureInvariant));
        Assert.Equal(33, Regex.Count(upOperation.Sql, "ALTER INDEX", RegexOptions.CultureInvariant));
        Assert.Equal(45, Regex.Count(downOperation.Sql, "RENAME CONSTRAINT", RegexOptions.CultureInvariant));
        Assert.Equal(33, Regex.Count(downOperation.Sql, "ALTER INDEX", RegexOptions.CultureInvariant));
        Assert.Contains("ALTER COLUMN \"Version\" DROP DEFAULT", upOperation.Sql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN \"Version\" SET DEFAULT 0", downOperation.Sql, StringComparison.Ordinal);

        foreach (var operation in new[] { upOperation, downOperation })
        {
            Assert.DoesNotContain("DROP TABLE", operation.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", operation.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE FROM", operation.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", operation.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void RuntimeModel_MapsEachModuleEntityToItsOwnedSchema()
    {
        using var dbContext = CreateDbContext();

        AssertTable(dbContext, typeof(User), "identity", "users");
        AssertTable(dbContext, typeof(Team), "teams", "teams");
        AssertTable(dbContext, "Mercurius.Modules.Teams.Domain.TeamMember", "teams", "team_members");
        AssertTable(dbContext, "Mercurius.Modules.Teams.Domain.TeamInvite", "teams", "team_invites");
        AssertTable(dbContext, "Mercurius.Modules.Tournament.Domain.Tournament", "tournament", "tournaments");
        AssertTable(dbContext, "Mercurius.Modules.Tournament.Domain.Match", "tournament", "matches");
        AssertTable(dbContext, "Mercurius.Modules.Tournament.Domain.TournamentRegistration", "tournament", "tournament_registrations");
        AssertTable(dbContext, "Mercurius.Modules.Tournament.Domain.TournamentRegistrationRosterMember", "tournament", "roster_members");
        AssertTable(dbContext, "Mercurius.Modules.Sponsorship.Domain.Sponsor", "sponsorship", "sponsors");
        AssertTable(dbContext, "Mercurius.Modules.Sponsorship.Domain.TournamentSponsorPlacement", "sponsorship", "tournament_sponsor_placements");
        AssertTable(dbContext, "Mercurius.Modules.Discovery.Domain.SearchDocument", "discovery", "search_documents");
    }

    [Fact]
    public void RuntimeModel_PreservesCrossModuleForeignKeyDeleteBehavior()
    {
        using var dbContext = CreateDbContext();

        var teamMember = GetEntityType(dbContext, "Mercurius.Modules.Teams.Domain.TeamMember");
        var teamInvite = GetEntityType(dbContext, "Mercurius.Modules.Teams.Domain.TeamInvite");
        var registration = GetEntityType(dbContext, "Mercurius.Modules.Tournament.Domain.TournamentRegistration");
        var placementUser = GetEntityType(dbContext, "Mercurius.Modules.Tournament.Domain.PlacementUser");

        AssertForeignKey(teamMember, typeof(User), "UserId", DeleteBehavior.Restrict);
        AssertForeignKey(teamMember, typeof(Team), "TeamId", DeleteBehavior.Cascade);
        AssertForeignKey(teamInvite, typeof(User), "UserId", DeleteBehavior.Restrict);
        AssertForeignKey(registration, typeof(User), "RegisteredByUserId", DeleteBehavior.Restrict);
        AssertForeignKey(registration, typeof(Team), "TeamId", DeleteBehavior.Restrict);
        AssertForeignKey(placementUser, typeof(User), "UserId", DeleteBehavior.Restrict);
    }

    [Fact]
    public void RuntimeModel_PreservesTeamsIdentityBoundaryMetadata()
    {
        using var dbContext = CreateDbContext();

        var team = Assert.IsAssignableFrom<IEntityType>(dbContext.Model.FindEntityType(typeof(Team)));
        var teamMember = GetEntityType(dbContext, "Mercurius.Modules.Teams.Domain.TeamMember");
        var teamInvite = GetEntityType(dbContext, "Mercurius.Modules.Teams.Domain.TeamInvite");

        Assert.Equal("teams", teamMember.GetSchema());
        Assert.Equal("team_members", teamMember.GetTableName());

        var primaryKey = Assert.IsAssignableFrom<IKey>(teamMember.FindPrimaryKey());
        Assert.Equal(["TeamId", "UserId"], primaryKey.Properties.Select(property => property.Name));
        Assert.Equal("PK_team_members", primaryKey.GetName());

        var userIndex = Assert.Single(teamMember.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(["UserId"]));
        Assert.Equal("IX_team_members_UserId", userIndex.GetDatabaseName());

        var memberTeamForeignKey = GetForeignKey(teamMember, typeof(Team), "TeamId");
        Assert.Equal("FK_team_members_teams_TeamId", memberTeamForeignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Cascade, memberTeamForeignKey.DeleteBehavior);
        Assert.Equal("Team", memberTeamForeignKey.DependentToPrincipal?.Name);
        Assert.Equal("Members", memberTeamForeignKey.PrincipalToDependent?.Name);

        var memberUserForeignKey = GetForeignKey(teamMember, typeof(User), "UserId");
        Assert.Equal("FK_team_members_users_UserId", memberUserForeignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, memberUserForeignKey.DeleteBehavior);
        Assert.Null(memberUserForeignKey.DependentToPrincipal);
        Assert.Null(memberUserForeignKey.PrincipalToDependent);

        var captainForeignKey = GetForeignKey(team, typeof(User), nameof(Team.CaptainUserId));
        Assert.Equal("FK_teams_users_CaptainUserId", captainForeignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.ClientSetNull, captainForeignKey.DeleteBehavior);
        Assert.Null(captainForeignKey.DependentToPrincipal);
        Assert.Null(captainForeignKey.PrincipalToDependent);

        var inviteUserForeignKey = GetForeignKey(teamInvite, typeof(User), "UserId");
        Assert.Equal("FK_team_invites_users_UserId", inviteUserForeignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, inviteUserForeignKey.DeleteBehavior);
        Assert.Null(inviteUserForeignKey.DependentToPrincipal);
        Assert.Null(inviteUserForeignKey.PrincipalToDependent);
    }

    [Fact]
    public void ModuleConfigurationClasses_ConfigureTheirOwnedEntityTypes()
    {
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Identity.IdentityModuleConfiguration).Assembly,
            "Mercurius.Modules.Identity.Infrastructure.UserConfiguration",
            typeof(User));
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Teams.TeamsModuleConfiguration).Assembly,
            "Mercurius.Modules.Teams.Infrastructure.TeamConfiguration",
            typeof(Team));
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Teams.TeamsModuleConfiguration).Assembly,
            "Mercurius.Modules.Teams.Infrastructure.TeamMemberConfiguration",
            "Mercurius.Modules.Teams.Domain.TeamMember");
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Tournament.TournamentModuleConfiguration).Assembly,
            "Mercurius.Modules.Tournament.Infrastructure.TournamentConfiguration",
            "Mercurius.Modules.Tournament.Domain.Tournament");
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Sponsorship.SponsorshipModuleConfiguration).Assembly,
            "Mercurius.Modules.Sponsorship.Infrastructure.SponsorConfiguration",
            "Mercurius.Modules.Sponsorship.Domain.Sponsor");
        AssertConfigurationEntity(
            typeof(Mercurius.Modules.Discovery.DiscoveryModuleConfiguration).Assembly,
            "Mercurius.Modules.Discovery.Infrastructure.SearchDocumentConfiguration",
            "Mercurius.Modules.Discovery.Domain.SearchDocument");
    }

    [Fact]
    public void ModuleSchemaOwnership_MovesExistingTablesWithoutDroppingThem()
    {
        var migration = new ModuleSchemaOwnership();
        var operations = migration.UpOperations.ToList();

        Assert.Contains(operations, operation => operation is EnsureSchemaOperation schema && schema.Name == "identity");
        Assert.Contains(operations, operation => operation is EnsureSchemaOperation schema && schema.Name == "teams");
        Assert.Contains(operations, operation => operation is EnsureSchemaOperation schema && schema.Name == "competition");
        Assert.Contains(operations, operation => operation is EnsureSchemaOperation schema && schema.Name == "sponsorship");
        Assert.DoesNotContain(operations, operation => operation is DropTableOperation);
        Assert.Contains(operations, operation =>
            operation is SqlOperation sql &&
            sql.Sql.Contains("ALTER TABLE public.\"Users\" SET SCHEMA identity", StringComparison.Ordinal) &&
            sql.Sql.Contains("ALTER TABLE public.\"TeamUser\" SET SCHEMA teams", StringComparison.Ordinal) &&
            sql.Sql.Contains("ALTER TABLE public.\"TournamentRegistrations\" SET SCHEMA competition", StringComparison.Ordinal) &&
            sql.Sql.Contains("ALTER TABLE public.\"GameSponsorPlacements\" SET SCHEMA sponsorship", StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleSchemaOwnership_DownMigrationRestoresLegacyTableLocations()
    {
        var migration = new ModuleSchemaOwnership();
        var operations = migration.DownOperations.ToList();

        Assert.Contains(operations, operation =>
            operation is SqlOperation sql &&
            sql.Sql.Contains("ALTER TABLE identity.users SET SCHEMA public", StringComparison.Ordinal) &&
            sql.Sql.Contains("ALTER TABLE competition.tournament_registrations SET SCHEMA public", StringComparison.Ordinal) &&
            sql.Sql.Contains("DROP SCHEMA identity", StringComparison.Ordinal));
    }

    private static MercuriusDBContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MercuriusDBContext>()
            .UseNpgsql("Host=localhost;Database=translation-only")
            .Options;

        return new MercuriusDBContext(options);
    }

    private static void AssertTable(MercuriusDBContext dbContext, Type entityType, string schema, string table)
    {
        var entity = dbContext.Model.FindEntityType(entityType);

        Assert.NotNull(entity);
        Assert.Equal(schema, entity.GetSchema());
        Assert.Equal(table, entity.GetTableName());
    }

    private static void AssertTable(MercuriusDBContext dbContext, string entityTypeName, string schema, string table)
    {
        var entity = GetEntityType(dbContext, entityTypeName);

        Assert.Equal(schema, entity.GetSchema());
        Assert.Equal(table, entity.GetTableName());
    }

    private static IEntityType GetEntityType(MercuriusDBContext dbContext, string entityTypeName)
    {
        return Assert.IsAssignableFrom<IEntityType>(dbContext.Model.FindEntityType(entityTypeName));
    }

    private static IForeignKey GetForeignKey(
        IEntityType dependentEntity,
        Type principalType,
        string foreignKeyProperty)
    {
        return Assert.Single(dependentEntity.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == principalType &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([foreignKeyProperty]));
    }

    private static void AssertForeignKey(
        IEntityType dependentEntity,
        Type principalType,
        string foreignKeyProperty,
        DeleteBehavior deleteBehavior)
    {
        Assert.Contains(dependentEntity.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == principalType &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual([foreignKeyProperty]) &&
            foreignKey.DeleteBehavior == deleteBehavior);
    }

    private static void AssertConfigurationEntity(Assembly assembly, string configurationTypeName, Type entityType)
    {
        var configurationType = assembly.GetType(configurationTypeName);

        Assert.NotNull(configurationType);
        Assert.Contains(configurationType.GetInterfaces(), @interface =>
            @interface.IsGenericType &&
            @interface.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>) &&
            @interface.GenericTypeArguments[0] == entityType);
    }

    private static void AssertConfigurationEntity(Assembly assembly, string configurationTypeName, string entityTypeName)
    {
        var configurationType = assembly.GetType(configurationTypeName);

        Assert.NotNull(configurationType);
        Assert.Contains(configurationType.GetInterfaces(), @interface =>
            @interface.IsGenericType &&
            @interface.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>) &&
            @interface.GenericTypeArguments[0].FullName == entityTypeName);
    }
}
