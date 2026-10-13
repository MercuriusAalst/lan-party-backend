using System.Reflection;
using System.Xml.Linq;
using Mercurius.Modules.Tournament.Application.Services;
using Mercurius.Modules.Identity.Application.Services;
using Platform.Eventing;

namespace Platform.Tests;

public class ModuleArchitectureTests
{
    private static readonly string[] ModuleNames =
    [
        "Tournament",
        "Discovery",
        "Identity",
        "Media",
        "Sponsorship",
        "Teams"
    ];

    public static IEnumerable<object[]> Modules =>
        ModuleNames.Select(moduleName => new object[] { moduleName });

    [Fact]
    public void ApiHost_ReferencesEveryModuleImplementationAndSharedInfrastructure()
    {
        var repositoryRoot = FindRepositoryRoot();
        var apiProject = Path.Combine(repositoryRoot, "src", "MercuriusAPI", "Mercurius.LAN.API.csproj");
        var references = GetProjectReferences(apiProject);
        var expectedReferences = ModuleNames
            .Select(moduleName => Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                moduleName,
                $"Mercurius.Modules.{moduleName}",
                $"Mercurius.Modules.{moduleName}.csproj"))
            .Append(Path.Combine(repositoryRoot, "src", "Modules.Shared", "Modules.Shared.csproj"))
            .Append(Path.Combine(repositoryRoot, "src", "Platform", "Platform.csproj"));

        Assert.All(expectedReferences, expectedReference => Assert.Contains(expectedReference, references));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleImplementation_ReferencesOnlyAllowedInfrastructure(string moduleName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var moduleDirectory = Path.Combine(repositoryRoot, "src", "Modules", moduleName);
        var implementationProject = Path.Combine(
            moduleDirectory,
            $"Mercurius.Modules.{moduleName}",
            $"Mercurius.Modules.{moduleName}.csproj");
        var references = GetProjectReferences(implementationProject);
        var requiredReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(
                moduleDirectory,
                $"Mercurius.Modules.{moduleName}.Contracts",
                $"Mercurius.Modules.{moduleName}.Contracts.csproj"),
            Path.Combine(repositoryRoot, "src", "Modules.Shared", "Modules.Shared.csproj")
        };
        var allowedReferences = new HashSet<string>(requiredReferences, StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(repositoryRoot, "src", "Platform", "Platform.csproj")
        };
        if (moduleName == "Teams")
        {
            var mediaContractsProject = Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Media",
                "Mercurius.Modules.Media.Contracts",
                "Mercurius.Modules.Media.Contracts.csproj");
            requiredReferences.Add(mediaContractsProject);
            allowedReferences.Add(mediaContractsProject);
            var identityContractsProject = Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Identity",
                "Mercurius.Modules.Identity.Contracts",
                "Mercurius.Modules.Identity.Contracts.csproj");
            requiredReferences.Add(identityContractsProject);
            allowedReferences.Add(identityContractsProject);
        }
        else if (moduleName == "Tournament")
        {
            allowedReferences.Add(Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Teams",
                "Mercurius.Modules.Teams.Contracts",
                "Mercurius.Modules.Teams.Contracts.csproj"));
            allowedReferences.Add(Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Identity",
                "Mercurius.Modules.Identity.Contracts",
                "Mercurius.Modules.Identity.Contracts.csproj"));
            allowedReferences.Add(Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Sponsorship",
                "Mercurius.Modules.Sponsorship.Contracts",
                "Mercurius.Modules.Sponsorship.Contracts.csproj"));
            allowedReferences.Add(Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Media",
                "Mercurius.Modules.Media.Contracts",
                "Mercurius.Modules.Media.Contracts.csproj"));
        }
        else if (moduleName == "Sponsorship")
        {
            allowedReferences.Add(Path.Combine(
                repositoryRoot,
                "src",
                "Modules",
                "Media",
                "Mercurius.Modules.Media.Contracts",
                "Mercurius.Modules.Media.Contracts.csproj"));
        }
        else if (moduleName == "Discovery")
        {
            foreach (var sourceModule in new[] { "Identity", "Teams", "Tournament", "Sponsorship" })
            {
                allowedReferences.Add(Path.Combine(
                    repositoryRoot,
                    "src",
                    "Modules",
                    sourceModule,
                    $"Mercurius.Modules.{sourceModule}.Contracts",
                    $"Mercurius.Modules.{sourceModule}.Contracts.csproj"));
            }
        }

        Assert.True(
            requiredReferences.IsSubsetOf(references) && references.IsSubsetOf(allowedReferences),
            $"Unexpected project references: {string.Join(", ", references)}");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleContracts_ReferenceOnlyModulesShared(string moduleName)
    {
        var repositoryRoot = FindRepositoryRoot();
        var contractsProject = Path.Combine(
            repositoryRoot,
            "src",
            "Modules",
            moduleName,
            $"Mercurius.Modules.{moduleName}.Contracts",
            $"Mercurius.Modules.{moduleName}.Contracts.csproj");
        var references = GetProjectReferences(contractsProject);
        var expectedReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(repositoryRoot, "src", "Modules.Shared", "Modules.Shared.csproj")
        };

        Assert.True(
            expectedReferences.SetEquals(references),
            $"Unexpected project references: {string.Join(", ", references)}");
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleContracts_ExposeModuleFacadeInterface(string moduleName)
    {
        var assembly = Assembly.Load($"Mercurius.Modules.{moduleName}.Contracts");
        var expectedInterfaceName = $"Mercurius.Modules.{moduleName}.Contracts.I{moduleName}Module";

        Assert.NotNull(assembly.GetType(expectedInterfaceName, throwOnError: false, ignoreCase: false));
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void ModuleContracts_DoNotExposeEntityFrameworkOrQueryableTypes(string moduleName)
    {
        var assembly = Assembly.Load($"Mercurius.Modules.{moduleName}.Contracts");
        var forbiddenTypes = assembly
            .GetExportedTypes()
            .SelectMany(GetPublicApiTypes)
            .Where(IsForbiddenContractType)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(typeName => typeName)
            .ToArray();

        Assert.True(
            forbiddenTypes.Length == 0,
            $"Contract assembly exposes forbidden types: {string.Join(", ", forbiddenTypes)}");
    }

    [Fact]
    public void ModulesShared_ExposesTypedIds()
    {
        var assembly = Assembly.Load("Modules.Shared");
        var expectedTypedIds = new Dictionary<string, Type>
        {
            ["TournamentId"] = typeof(Guid),
            ["MatchId"] = typeof(Guid),
            ["SponsorId"] = typeof(int),
            ["SponsorPlacementId"] = typeof(int),
            ["TeamId"] = typeof(Guid),
            ["TeamInviteId"] = typeof(Guid),
            ["TournamentRegistrationId"] = typeof(Guid),
            ["TournamentRosterMemberId"] = typeof(Guid),
            ["UserId"] = typeof(Guid)
        };

        foreach (var (typeName, valueType) in expectedTypedIds)
        {
            var type = assembly.GetType($"Mercurius.Modules.Shared.{typeName}", throwOnError: false, ignoreCase: false);

            Assert.NotNull(type);
            Assert.True(type!.IsValueType, $"{typeName} must be a value type.");
            Assert.Equal(valueType, type.GetProperty("Value")?.PropertyType);
        }
    }

    [Fact]
    public void IdentityUserService_DoesNotPublishIntegrationEventsDirectly()
    {
        var userServiceDependencies = GetDeclaredDependencyTypes(typeof(UserService));
        var publishingDecoratorDependencies = GetDeclaredDependencyTypes(typeof(UserIntegrationEventPublishingService));

        Assert.DoesNotContain(typeof(IModuleEventPublisher), userServiceDependencies);
        Assert.Contains(typeof(IModuleEventPublisher), publishingDecoratorDependencies);
    }

    [Fact]
    public void TeamsImplementationTypes_RemainNonPublicAndLegacyFallbacksAreRemoved()
    {
        var assembly = Assembly.Load("Mercurius.Modules.Teams");
        var nonPublicTypeNames = new[]
        {
            "Mercurius.Modules.Teams.Application.Services.TeamService",
            "Mercurius.Modules.Teams.Application.Services.TeamEventPublishingDecorator",
            "Mercurius.Modules.Teams.Application.Services.ITeamQueries",
            "Mercurius.Modules.Teams.Application.Services.ITeamManagementCommands",
            "Mercurius.Modules.Teams.Application.Services.ITeamInviteWorkflows",
            "Mercurius.Modules.Teams.Application.Services.ITeamLogoCommands",
            "Mercurius.Modules.Teams.Application.Services.ITeamEndpointService",
            "Mercurius.Modules.Teams.Application.Services.TeamEndpointService",
            "Mercurius.Modules.Teams.Application.Services.RealtimeTeamEventPublisher",
            "Mercurius.Modules.Teams.Application.Services.EfTeamRealtimeAuthorizer",
            "Mercurius.Modules.Teams.Domain.TeamInvite",
            "Mercurius.Modules.Teams.Domain.TeamMember",
            "Mercurius.Modules.Teams.Infrastructure.ITeamsDbContext",
            "Mercurius.Modules.Teams.Infrastructure.TeamsDbContextAdapter`1",
            "Mercurius.Modules.Teams.Infrastructure.TeamConfiguration",
            "Mercurius.Modules.Teams.Infrastructure.TeamMemberConfiguration",
            "Mercurius.Modules.Teams.Infrastructure.TeamInviteConfiguration"
        };

        foreach (var typeName in nonPublicTypeNames)
        {
            var type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);

            Assert.NotNull(type);
            Assert.False(type!.IsPublic, $"{typeName} must remain non-public.");
        }

        Assert.Null(assembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamService", throwOnError: false));
        Assert.Null(assembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamApplicationService", throwOnError: false));
        Assert.Null(assembly.GetType("Mercurius.Modules.Teams.Application.Services.NullTeamEventPublisher", throwOnError: false));
        Assert.Null(assembly.GetType("Mercurius.Modules.Teams.Application.Services.NullTeamTournamentReadService", throwOnError: false));
    }

    [Fact]
    public void TeamsImplementation_DependsOnlyOnIdentityContractsAndDoesNotExposeIdentityPersistence()
    {
        var teamsAssembly = Assembly.Load("Mercurius.Modules.Teams");
        var referencedAssemblies = teamsAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Mercurius.Modules.Identity.Contracts", referencedAssemblies);
        Assert.DoesNotContain("Mercurius.Modules.Identity", referencedAssemblies);

        var teamsDbContext = teamsAssembly.GetType(
            "Mercurius.Modules.Teams.Infrastructure.ITeamsDbContext",
            throwOnError: true)!;
        const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Assert.Null(teamsDbContext.GetProperty("Users", members));
        Assert.Null(teamsDbContext.GetProperty("ChangeTracker", members));
        Assert.DoesNotContain(teamsDbContext.GetMethods(members), method => method.Name == "Entry");
    }

    [Fact]
    public void TeamsApplicationContracts_RequireTrailingCancellationToken()
    {
        var teamsAssembly = Assembly.Load("Mercurius.Modules.Teams");
        var interfaces = new[]
        {
            "Mercurius.Modules.Teams.Application.Services.ITeamQueries",
            "Mercurius.Modules.Teams.Application.Services.ITeamManagementCommands",
            "Mercurius.Modules.Teams.Application.Services.ITeamInviteWorkflows",
            "Mercurius.Modules.Teams.Application.Services.ITeamLogoCommands",
            "Mercurius.Modules.Teams.Application.Services.ITeamEndpointService"
        }
            .Select(typeName => teamsAssembly.GetType(typeName, throwOnError: true)!)
            .ToArray();

        foreach (var interfaceType in interfaces)
        {
            var asyncMethods = interfaceType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType));

            foreach (var method in asyncMethods)
            {
                var cancellationToken = method.GetParameters().Last();

                Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
                Assert.True(
                    cancellationToken.HasDefaultValue,
                    $"{interfaceType.Name}.{method.Name} should default its cancellation token.");
            }
        }
    }

    [Fact]
    public void TeamsContracts_AreInternalAndSegregated()
    {
        var teamsAssembly = Assembly.Load("Mercurius.Modules.Teams");
        var expectedMethodsByType = new Dictionary<string, string[]>
        {
            ["Mercurius.Modules.Teams.Application.Services.ITeamQueries"] =
            [
                "GetAllTeamsAsync",
                "GetPublicTeamProfileAsync",
                "GetTeamByIdAsync"
            ],
            ["Mercurius.Modules.Teams.Application.Services.ITeamManagementCommands"] =
            [
                "CreateCurrentUserTeamAsync",
                "DeleteTeamAsync",
                "LeaveTeamAsync",
                "RemoveMemberAsync",
                "TransferCaptainAsync"
            ],
            ["Mercurius.Modules.Teams.Application.Services.ITeamInviteWorkflows"] =
            [
                "CancelInviteAsync",
                "GetCurrentUserInvitesAsync",
                "GetCurrentUserSentInvitesAsync",
                "GetCurrentUserTeamSummaryAsync",
                "InviteUserAsync",
                "RespondToInviteAsync"
            ],
            ["Mercurius.Modules.Teams.Application.Services.ITeamLogoCommands"] =
            [
                "RemoveTeamLogoAsync",
                "UploadTeamLogoAsync"
            ]
        };

        foreach (var (typeName, expectedMethods) in expectedMethodsByType)
        {
            var type = teamsAssembly.GetType(typeName, throwOnError: true)!;
            var actualMethods = type.GetMethods().Select(method => method.Name);

            Assert.False(type.IsPublic, $"{typeName} must remain an internal application contract.");
            Assert.Equal(
                expectedMethods.OrderBy(method => method, StringComparer.Ordinal),
                actualMethods.OrderBy(method => method, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TeamApplicationImplementations_UseOnlyTheirNecessaryFocusedContracts()
    {
        var teamsAssembly = Assembly.Load("Mercurius.Modules.Teams");
        var queriesType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamQueries", throwOnError: true)!;
        var commandsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamManagementCommands", throwOnError: true)!;
        var inviteWorkflowsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamInviteWorkflows", throwOnError: true)!;
        var logoCommandsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamLogoCommands", throwOnError: true)!;
        var teamServiceType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.TeamService", throwOnError: true)!;
        var decoratorType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.TeamEventPublishingDecorator", throwOnError: true)!;

        Assert.Contains(queriesType, teamServiceType.GetInterfaces());
        Assert.Contains(commandsType, teamServiceType.GetInterfaces());
        Assert.Contains(inviteWorkflowsType, teamServiceType.GetInterfaces());
        Assert.Contains(logoCommandsType, teamServiceType.GetInterfaces());

        Assert.DoesNotContain(queriesType, decoratorType.GetInterfaces());
        Assert.Contains(commandsType, decoratorType.GetInterfaces());
        Assert.Contains(inviteWorkflowsType, decoratorType.GetInterfaces());
        Assert.DoesNotContain(logoCommandsType, decoratorType.GetInterfaces());
    }

    [Fact]
    public void TeamEndpointService_DependsOnFocusedTeamApplicationContracts()
    {
        var teamsAssembly = Assembly.Load("Mercurius.Modules.Teams");
        var endpointServiceType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.TeamEndpointService", throwOnError: true)!;
        var queriesType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamQueries", throwOnError: true)!;
        var commandsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamManagementCommands", throwOnError: true)!;
        var inviteWorkflowsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamInviteWorkflows", throwOnError: true)!;
        var logoCommandsType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.ITeamLogoCommands", throwOnError: true)!;
        var decoratorType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.TeamEventPublishingDecorator", throwOnError: true)!;
        var teamServiceType = teamsAssembly.GetType("Mercurius.Modules.Teams.Application.Services.TeamService", throwOnError: true)!;

        var dependencyTypes = GetDeclaredDependencyTypes(endpointServiceType);

        Assert.Contains(queriesType, dependencyTypes);
        Assert.Contains(commandsType, dependencyTypes);
        Assert.Contains(inviteWorkflowsType, dependencyTypes);
        Assert.Contains(logoCommandsType, dependencyTypes);
        Assert.DoesNotContain(decoratorType, dependencyTypes);
        Assert.DoesNotContain(teamServiceType, dependencyTypes);
    }

    [Fact]
    public void TournamentEndpointTypes_AreInternalAndApiHostNoLongerOwnsThem()
    {
        var tournamentAssembly = Assembly.Load("Mercurius.Modules.Tournament");
        var apiAssembly = Assembly.Load("Mercurius.LAN.API");
        var endpointTypeNames = new[]
        {
            "Mercurius.Modules.Tournament.Endpoints.TournamentEndpoints",
            "Mercurius.Modules.Tournament.Endpoints.MatchEndpoints",
            "Mercurius.Modules.Tournament.Endpoints.TournamentRegistrationEndpoints"
        };

        foreach (var typeName in endpointTypeNames)
        {
            var endpointType = tournamentAssembly.GetType(typeName, throwOnError: false, ignoreCase: false);

            Assert.NotNull(endpointType);
            Assert.False(endpointType!.IsPublic, $"{typeName} must remain an internal module implementation detail.");
        }

        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Endpoints.TournamentEndpoints", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Endpoints.MatchEndpoints", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Endpoints.TournamentRegistrationEndpoints", throwOnError: false));
    }

    [Fact]
    public void SponsorshipImplementationTypes_AreInternalAndApiHostNoLongerOwnsThem()
    {
        var sponsorshipAssembly = Assembly.Load("Mercurius.Modules.Sponsorship");
        var apiAssembly = Assembly.Load("Mercurius.LAN.API");
        var implementationTypeNames = new[]
        {
            "Mercurius.Modules.Sponsorship.Domain.Sponsor",
            "Mercurius.Modules.Sponsorship.Domain.TournamentSponsorPlacement",
            "Mercurius.Modules.Sponsorship.Application.Services.ISponsorService",
            "Mercurius.Modules.Sponsorship.Application.Services.SponsorService",
            "Mercurius.Modules.Sponsorship.Infrastructure.ISponsorshipDbContext",
            "Mercurius.Modules.Sponsorship.Infrastructure.SponsorshipDbContextAdapter`1",
            "Mercurius.Modules.Sponsorship.Endpoints.SponsorEndpoints"
        };

        foreach (var typeName in implementationTypeNames)
        {
            var type = sponsorshipAssembly.GetType(typeName, throwOnError: false, ignoreCase: false);

            Assert.NotNull(type);
            Assert.False(type!.IsPublic, $"{typeName} must remain an internal module implementation detail.");
        }

        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Endpoints.SponsorEndpoints", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Models.Sponsor", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Models.TournamentSponsorPlacement", throwOnError: false));
    }

    [Fact]
    public void MediaImplementationTypes_AreInternalAndApiHostNoLongerOwnsFileServices()
    {
        var mediaAssembly = Assembly.Load("Mercurius.Modules.Media");
        var apiAssembly = Assembly.Load("Mercurius.LAN.API");
        var storageType = mediaAssembly.GetType(
            "Mercurius.Modules.Media.Infrastructure.FileSystemMediaModule",
            throwOnError: false,
            ignoreCase: false);

        Assert.NotNull(storageType);
        Assert.False(storageType!.IsPublic, "FileSystemMediaModule must remain an internal module implementation detail.");
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Composition.LegacyMediaModuleAdapter", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Services.Files.FileService", throwOnError: false));
        Assert.Null(apiAssembly.GetType("Mercurius.LAN.API.Services.Files.FileValidationService", throwOnError: false));
    }

    [Fact]
    public void TournamentApplicationServices_RequireTrailingCancellationToken()
    {
        var tournamentAssembly = Assembly.Load("Mercurius.Modules.Tournament");
        var interfaces = new[]
        {
            "Mercurius.Modules.Tournament.Application.Services.ITournamentQueries",
            "Mercurius.Modules.Tournament.Application.Services.ITournamentManagementCommands",
            "Mercurius.Modules.Tournament.Application.Services.ITournamentLifecycleCommands",
            "Mercurius.Modules.Tournament.Application.Services.IMatchService",
            "Mercurius.Modules.Tournament.Application.Services.ITournamentRegistrationService"
        }
            .Select(typeName => tournamentAssembly.GetType(typeName, throwOnError: true)!)
            .ToArray();

        foreach (var interfaceType in interfaces)
        {
            var asyncMethods = interfaceType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType));

            foreach (var method in asyncMethods)
            {
                var cancellationToken = method.GetParameters().Last();

                Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
                Assert.True(
                    cancellationToken.HasDefaultValue,
                    $"{interfaceType.Name}.{method.Name} should default its cancellation token.");
            }
        }

        Assert.Null(tournamentAssembly.GetType("Mercurius.Modules.Tournament.Application.Services.ITournamentService", throwOnError: false));
    }

    [Fact]
    public void TournamentContracts_AreInternalAndSegregated()
    {
        var tournamentAssembly = Assembly.Load("Mercurius.Modules.Tournament");
        var expectedMethodsByType = new Dictionary<string, string[]>
        {
            ["Mercurius.Modules.Tournament.Application.Services.ITournamentQueries"] =
            [
                "GetAllTournamentsAsync",
                "GetTournamentByIdAsync"
            ],
            ["Mercurius.Modules.Tournament.Application.Services.ITournamentManagementCommands"] =
            [
                "CreateTournamentAsync",
                "DeleteTournamentAsync",
                "ReplaceSponsorPlacementsAsync",
                "UpdateTournamentAsync"
            ],
            ["Mercurius.Modules.Tournament.Application.Services.ITournamentLifecycleCommands"] =
            [
                "CancelTournamentAsync",
                "CompleteTournamentAsync",
                "ResetTournamentAsync",
                "StartTournamentAsync"
            ]
        };

        foreach (var (typeName, expectedMethods) in expectedMethodsByType)
        {
            var type = tournamentAssembly.GetType(typeName, throwOnError: true)!;

            Assert.False(type.IsPublic, $"{typeName} must remain an internal application contract.");
            Assert.Equal(
                expectedMethods.OrderBy(method => method, StringComparer.Ordinal),
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Select(method => method.Name)
                    .OrderBy(method => method, StringComparer.Ordinal));
        }
    }

    private static HashSet<string> GetProjectReferences(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = XDocument.Load(projectPath);

        return project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(
                projectDirectory,
                include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Type[] GetDeclaredDependencyTypes(Type type)
    {
        const BindingFlags declaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        return type
            .GetConstructors(declaredMembers)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Concat(type.GetFields(declaredMembers).Select(field => field.FieldType))
            .ToArray();
    }

    private static IEnumerable<Type> GetPublicApiTypes(Type exportedType)
    {
        yield return exportedType;

        if (exportedType.BaseType is not null)
        {
            yield return exportedType.BaseType;
        }

        foreach (var interfaceType in exportedType.GetInterfaces())
        {
            yield return interfaceType;
        }

        const BindingFlags publicDeclaredMembers =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var constructor in exportedType.GetConstructors(publicDeclaredMembers))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var method in exportedType.GetMethods(publicDeclaredMembers))
        {
            yield return method.ReturnType;

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var property in exportedType.GetProperties(publicDeclaredMembers))
        {
            yield return property.PropertyType;
        }

        foreach (var field in exportedType.GetFields(publicDeclaredMembers))
        {
            yield return field.FieldType;
        }

        foreach (var eventInfo in exportedType.GetEvents(publicDeclaredMembers))
        {
            if (eventInfo.EventHandlerType is not null)
            {
                yield return eventInfo.EventHandlerType;
            }
        }
    }

    private static bool IsForbiddenContractType(Type type)
    {
        if (type.HasElementType)
        {
            return IsForbiddenContractType(type.GetElementType()!);
        }

        if (type == typeof(IQueryable)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
            || typeof(IQueryable).IsAssignableFrom(type))
        {
            return true;
        }

        if (type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(IsForbiddenContractType);
    }

    private static string FindRepositoryRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "LAN.API.sln")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing LAN.API.sln.");
    }
}
