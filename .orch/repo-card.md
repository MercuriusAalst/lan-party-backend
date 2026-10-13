# repo-card: mercurius-aalst-back-end updated: 2026-10-13 by: scout/architect approved: 2026-10-13
STACK: .NET 10, ASP.NET Core minimal APIs, modular monolith, EF Core 10, PostgreSQL, xUnit
BUILD: dotnet build LAN.API.sln
CHECK_FAST: dotnet test tests/Mercurius.Modules.Tournament.Tests/Mercurius.Modules.Tournament.Tests.csproj --filter FullyQualifiedName~Match
VERIFY: dotnet build LAN.API.sln && dotnet test LAN.API.sln
UI_SHOTS:
SPEC_WORKFLOW: openspec
SPEC_PROFILE: mercurius-lan
PR_TEMPLATE: none confirmed
PR_FORMAT: default
PROTECTED: main
RUNTIME: containers, assume 2+ replicas, rolling deployment, managed PostgreSQL
HOT_SPOTS: Tournament Domain/Match.cs; MatchService; deadlines; rowversion; migrations; authentication
RELATED_REPOS: frontend=D:/Github Repositories/lan-party; helm=D:/Github Repositories/lan-party-helmchart
CONVENTIONS:
- Server enforces authorization, validation, deadlines and lifecycle transitions.
- Preserve optimistic concurrency, persisted deadlines, transactional bracket advancement and existing outbox.
- Keep route and DTO shapes compatible during rolling deployments.
- No new packages, projects or Shared types without architect review.

## STRUCTURE (approved 2026-10-13; existing layout)
SHAPE: modules
CORE: Domain and Application folders within module projects
PROJECTS: MercuriusAPI host -> modules and Platform; modules -> own Contracts, other Contracts, Modules.Shared and Platform; test projects -> their target projects
FOLDERS: src/Modules/<Module>/Mercurius.Modules.<Module>/{Domain,Application,Infrastructure,Endpoints}/; separate Mercurius.Modules.<Module>.Contracts projects
MODULE-WIDE: module Infrastructure/; host Migrations/ remains existing location
CROSS-CUTTING: src/Platform/; src/Modules.Shared/
SHARED: src/Modules.Shared/; reuse existing types; additions need architect review
NAMESPACE: matches project/folder conventions
TESTS: tests/Mercurius.Modules.<Module>.Tests/; tests/Mercurius.Api.Tests/; existing flat files
NEW PROJECT OR MODULE: STOP IF; architect decides
EXAMPLES:
- src/Modules/Tournament/Mercurius.Modules.Tournament/Domain/Match.cs
- src/Modules/Tournament/Mercurius.Modules.Tournament/Application/Services/MatchService.cs
- src/Modules/Tournament/Mercurius.Modules.Tournament/Endpoints/MatchEndpoints.cs
- tests/Mercurius.Modules.Tournament.Tests/MatchTests.cs
EXCEPTIONS:
- src/MercuriusAPI/Migrations/ centralized migrations: leave as is.
