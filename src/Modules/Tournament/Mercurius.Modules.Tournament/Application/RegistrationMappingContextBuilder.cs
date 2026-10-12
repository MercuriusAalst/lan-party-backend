using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;

namespace Mercurius.Modules.Tournament.Application;

internal sealed class RegistrationMappingContextBuilder(
    IIdentityModule identityModule,
    ITeamsModule teamsModule)
{
    public async Task<RegistrationMappingContext> BuildAsync(
        IReadOnlyCollection<TournamentRegistration> registrations,
        IReadOnlyCollection<Placement> placements,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? additionalUserIds = null)
    {
        var userIds = registrations
            .SelectMany(registration =>
                registration.RosterMembers.Select(member => member.UserId)
                    .Concat(registration.UserId.HasValue ? [registration.UserId.Value] : []))
            .Concat(placements.SelectMany(placement => placement.Users.Select(user => user.UserId)))
            .Concat(additionalUserIds ?? [])
            .Distinct()
            .Select(userId => new UserId(userId))
            .ToArray();
        var teamIds = registrations
            .Where(registration => registration.TeamId.HasValue)
            .Select(registration => registration.TeamId!.Value)
            .Concat(placements.SelectMany(placement => placement.Teams.Select(team => team.TeamId)))
            .Distinct()
            .Select(teamId => new TeamId(teamId))
            .ToArray();

        var users = await identityModule.GetUsersByIdsAsync(userIds, cancellationToken);
        var teams = await teamsModule.GetTeamRosterSnapshotsAsync(teamIds, cancellationToken);

        return new RegistrationMappingContext(
            users,
            teams);
    }
}
