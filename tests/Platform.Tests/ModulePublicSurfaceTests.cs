using System.Reflection;
using System.Runtime.CompilerServices;

namespace Platform.Tests;

public sealed class ModulePublicSurfaceTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedFriendAssemblies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Tournament"] = ["Mercurius.Api.Tests", "Mercurius.Modules.Tournament.Tests", "Mercurius.Modules.Sponsorship.Tests", "Mercurius.Modules.Teams.Tests"],
            ["Discovery"] = ["Mercurius.Modules.Discovery.Tests"],
            ["Identity"] = ["Mercurius.Api.Tests", "Mercurius.Modules.Tournament.Tests", "Mercurius.Modules.Identity.Tests", "Mercurius.Modules.Teams.Tests", "Platform.Tests"],
            ["Media"] = [],
            ["Sponsorship"] = ["Mercurius.Modules.Tournament.Tests", "Mercurius.Modules.Sponsorship.Tests"],
            ["Teams"] = ["Mercurius.Api.Tests", "Mercurius.Modules.Tournament.Tests", "Mercurius.Modules.Teams.Tests", "Platform.Tests"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedPublicTypes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Tournament"] = ["Mercurius.Modules.Tournament.TournamentModuleConfiguration"],
            ["Discovery"] = ["Mercurius.Modules.Discovery.DiscoveryModuleConfiguration"],
            ["Identity"] =
            [
                "Mercurius.Modules.Identity.IdentityModuleConfiguration",
                "Mercurius.Modules.Identity.Domain.User"
            ],
            ["Media"] = ["Mercurius.Modules.Media.MediaModuleConfiguration"],
            ["Sponsorship"] = ["Mercurius.Modules.Sponsorship.SponsorshipModuleConfiguration"],
            ["Teams"] =
            [
                "Mercurius.Modules.Teams.TeamsModuleConfiguration",
                "Mercurius.Modules.Teams.Domain.Team"
            ]
        };

    public static IEnumerable<object[]> Modules => ExpectedPublicTypes.Keys.Select(moduleName => new object[] { moduleName });

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleImplementation_ExportsOnlyCompositionRootsAndDocumentedTemporaryPersistenceTypes(string moduleName)
    {
        var assembly = Assembly.Load($"Mercurius.Modules.{moduleName}");
        var exportedTypeNames = assembly
            .GetExportedTypes()
            .Select(type => type.FullName ?? type.Name)
            .OrderBy(typeName => typeName, StringComparer.Ordinal)
            .ToArray();
        var expectedTypeNames = ExpectedPublicTypes[moduleName]
            .OrderBy(typeName => typeName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedTypeNames, exportedTypeNames);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleImplementation_GrantsInternalAccessOnlyToTestAssemblies(string moduleName)
    {
        var assembly = Assembly.Load($"Mercurius.Modules.{moduleName}");
        var friendAssemblyNames = assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => new AssemblyName(attribute.AssemblyName).Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!);

        Assert.Equal(
            ExpectedFriendAssemblies[moduleName].OrderBy(name => name, StringComparer.Ordinal),
            friendAssemblyNames.OrderBy(name => name, StringComparer.Ordinal));
    }
}
