using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Teams.Contracts;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Mercurius.Modules.Tournament.Application.Services;

// Applies the deleted-user participation policy. Only Scheduled tournaments change: in-progress tournaments keep
// their registrations, rosters and matches for bracket integrity, and completed or canceled ones are history.
// Every changed tournament increments its persisted revision, so cleanup cannot commit together with a bracket
// start based on stale registrations; a conflict fails the event so the outbox retries it.
internal sealed class DeletedUserTournamentCleanupService
{
    private const string WithdrawnStatus = "Withdrawn";
    private readonly ITournamentDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamsModule _teamsModule;
    private readonly ITournamentRealtimePublisher _realtimePublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeletedUserTournamentCleanupService> _logger;

    public DeletedUserTournamentCleanupService(
        ITournamentDbContext dbContext,
        IIdentityModule identityModule,
        ITeamsModule teamsModule,
        ITournamentRealtimePublisher realtimePublisher,
        TimeProvider timeProvider,
        ILogger<DeletedUserTournamentCleanupService> logger)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
        _teamsModule = teamsModule;
        _realtimePublisher = realtimePublisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> RemoveDeletedUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _identityModule.GetUserProfileAsync(new UserId(userId), cancellationToken);
        if (user is not { IsDeleted: true })
        {
            _logger.LogWarning("Skipped tournament cleanup for user {UserId} because the account is not deleted.", userId);
            return 0;
        }

        var withdrawn = new List<WithdrawnRosterMember>();
        int changedRegistrations;
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var now = UtcNow();
            var individualRegistrations = await _dbContext.TournamentRegistrations
                .Include(registration => registration.Tournament)
                .Where(registration =>
                    registration.UserId == userId &&
                    registration.Kind == TournamentRegistrationKind.Individual &&
                    registration.Tournament.Status == TournamentStatus.Scheduled)
                .ToListAsync(cancellationToken);
            var teamRegistrations = await _dbContext.TournamentRegistrations
                .AsSplitQuery()
                .Include(registration => registration.Tournament)
                .Include(registration => registration.RosterMembers)
                .Where(registration =>
                    registration.Kind == TournamentRegistrationKind.Team &&
                    registration.Tournament.Status == TournamentStatus.Scheduled &&
                    registration.RosterMembers.Any(member => member.UserId == userId))
                .ToListAsync(cancellationToken);

            foreach (var registration in individualRegistrations)
            {
                registration.Tournament.IncrementRevision();
                _dbContext.TournamentRegistrations.Remove(registration);
            }

            foreach (var registration in teamRegistrations)
            {
                registration.Tournament.IncrementRevision();
                var (_, wasCaptain) = RemoveRosterRows(registration, userId, withdrawn);
                MarkPendingConfirmation(registration, now);
                if (!wasCaptain || !registration.TeamId.HasValue)
                    continue;

                // The team's succession decides the roster's new captain. When Teams has not appointed one yet,
                // the captain-succession event re-points or removes this registration later.
                var team = await _teamsModule.GetTeamSummaryAsync(new TeamId(registration.TeamId.Value), cancellationToken);
                if (team?.CaptainUserId is { } currentCaptain && currentCaptain.Value != userId)
                    await RePointCaptainAsync(registration, currentCaptain.Value, now, cancellationToken);
            }

            changedRegistrations = individualRegistrations.Count + teamRegistrations.Count;
            if (changedRegistrations > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishWithdrawnAsync(withdrawn, cancellationToken);
        if (changedRegistrations > 0)
        {
            _logger.LogInformation(
                "Removed deleted user {UserId} from {RegistrationCount} scheduled tournament registrations.",
                userId,
                changedRegistrations);
        }

        return changedRegistrations;
    }

    public async Task<int> ApplyCaptainSuccessionAsync(
        Guid teamId,
        Guid deletedCaptainUserId,
        Guid? newCaptainUserId,
        CancellationToken cancellationToken = default)
    {
        var withdrawn = new List<WithdrawnRosterMember>();
        int changedRegistrations;
        await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var now = UtcNow();
            var registrations = await _dbContext.TournamentRegistrations
                .AsSplitQuery()
                .Include(registration => registration.Tournament)
                .Include(registration => registration.RosterMembers)
                .Where(registration =>
                    registration.TeamId == teamId &&
                    registration.Kind == TournamentRegistrationKind.Team &&
                    registration.Tournament.Status == TournamentStatus.Scheduled)
                .ToListAsync(cancellationToken);
            var team = registrations.Count == 0
                ? null
                : await _teamsModule.GetTeamSummaryAsync(new TeamId(teamId), cancellationToken);
            var currentCaptainUserId = team is { IsDeleted: false } ? team.CaptainUserId?.Value : null;

            changedRegistrations = 0;
            foreach (var registration in registrations)
            {
                if (currentCaptainUserId.HasValue)
                {
                    // Only rosters still led by the deleted captain are re-pointed; others are left untouched.
                    var (removed, _) = RemoveRosterRows(registration, deletedCaptainUserId, withdrawn);
                    if (!removed && registration.TeamCaptainUserIdAtRegistration != deletedCaptainUserId)
                        continue;

                    registration.Tournament.IncrementRevision();
                    if (removed)
                        MarkPendingConfirmation(registration, now);
                    await RePointCaptainAsync(registration, currentCaptainUserId.Value, now, cancellationToken);
                    changedRegistrations++;
                }
                else if (!newCaptainUserId.HasValue)
                {
                    // No eligible successor: nobody can manage this roster any more, so the registration goes.
                    registration.Tournament.IncrementRevision();
                    withdrawn.AddRange(registration.RosterMembers
                        .Where(member => member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending)
                        .Select(member => new WithdrawnRosterMember(teamId, member.Id, member.UserId)));
                    _dbContext.TournamentRegistrations.Remove(registration);
                    changedRegistrations++;
                    _logger.LogWarning(
                        "Removed scheduled registration {RegistrationId} of team {TeamId} because its deleted captain {DeletedUserId} has no eligible successor.",
                        registration.Id,
                        teamId,
                        deletedCaptainUserId);
                }
                else if (RemoveRosterRows(registration, deletedCaptainUserId, withdrawn).Removed)
                {
                    // The appointed successor was deleted in turn; that deletion's succession settles this roster.
                    registration.Tournament.IncrementRevision();
                    MarkPendingConfirmation(registration, now);
                    changedRegistrations++;
                }
            }

            if (changedRegistrations > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await PublishWithdrawnAsync(withdrawn, cancellationToken);
        return changedRegistrations;
    }

    // One reconciliation page: users with scheduled individual or roster participation, ordered by id, narrowed to
    // accounts that Identity reports as deleted. NextAfterUserId is null on the last page.
    public async Task<DeletedUserPage> FindDeletedUsersWithScheduledParticipationAsync(
        Guid? afterUserId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = await _dbContext.TournamentRegistrations
            .Where(registration =>
                registration.UserId.HasValue &&
                registration.Tournament.Status == TournamentStatus.Scheduled)
            .Select(registration => registration.UserId!.Value)
            .Concat(_dbContext.TournamentRegistrationRosterMembers
                .Where(member => member.Tournament.Status == TournamentStatus.Scheduled)
                .Select(member => member.UserId))
            .Distinct()
            .Where(candidateId => !afterUserId.HasValue || candidateId > afterUserId.Value)
            .OrderBy(candidateId => candidateId)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        if (page.Count == 0)
            return new DeletedUserPage([], null);

        var users = await _identityModule.GetUsersByIdsAsync(
            page.Select(candidateId => new UserId(candidateId)).ToArray(),
            cancellationToken);
        var deletedUserIds = page
            .Where(candidateId => users.TryGetValue(new UserId(candidateId), out var candidate) && candidate.IsDeleted)
            .ToList();
        return new DeletedUserPage(deletedUserIds, page.Count < pageSize ? null : page[^1]);
    }

    private (bool Removed, bool WasCaptain) RemoveRosterRows(
        TournamentRegistration registration,
        Guid userId,
        List<WithdrawnRosterMember> withdrawn)
    {
        var rows = registration.RosterMembers.Where(member => member.UserId == userId).ToList();
        foreach (var row in rows)
        {
            if (row.ConfirmationStatus == RosterMemberConfirmationStatus.Pending && registration.TeamId.HasValue)
                withdrawn.Add(new WithdrawnRosterMember(registration.TeamId.Value, row.Id, row.UserId));
            registration.RosterMembers.Remove(row);
            _dbContext.TournamentRegistrationRosterMembers.Remove(row);
        }

        return (rows.Count > 0, rows.Any(row => row.IsCaptain));
    }

    private static void MarkPendingConfirmation(TournamentRegistration registration, DateTime now)
    {
        registration.Status = TournamentRegistrationStatus.PendingConfirmation;
        registration.UpdatedAtUtc = now;
    }

    private async Task RePointCaptainAsync(
        TournamentRegistration registration,
        Guid captainUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var captain = await _identityModule.GetUserProfileAsync(new UserId(captainUserId), cancellationToken);
        registration.TeamCaptainUserIdAtRegistration = captainUserId;
        registration.RegisteredByUserId = captainUserId;
        registration.RegisteredByUsernameAtRegistration = captain?.Username ?? string.Empty;
        registration.UpdatedAtUtc = now;
        foreach (var member in registration.RosterMembers)
        {
            var isCaptain = member.UserId == captainUserId;
            if (member.IsCaptain == isCaptain)
                continue;

            member.IsCaptain = isCaptain;
            member.UpdatedAtUtc = now;
            if (isCaptain && member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending)
            {
                member.ConfirmationStatus = RosterMemberConfirmationStatus.AutoConfirmed;
                member.ConfirmedAtUtc = now;
            }
        }
    }

    private async Task PublishWithdrawnAsync(
        IReadOnlyCollection<WithdrawnRosterMember> withdrawn,
        CancellationToken cancellationToken)
    {
        foreach (var member in withdrawn)
        {
            try
            {
                await _realtimePublisher.RosterConfirmationChangedAsync(
                    member.TeamId,
                    member.RosterMemberId,
                    member.UserId,
                    WithdrawnStatus,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Roster confirmation realtime notification failed after deleted-user cleanup for roster member {RosterMemberId}.",
                    member.RosterMemberId);
            }
        }
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    internal sealed record DeletedUserPage(IReadOnlyList<Guid> DeletedUserIds, Guid? NextAfterUserId);

    private sealed record WithdrawnRosterMember(Guid TeamId, Guid RosterMemberId, Guid UserId);
}
