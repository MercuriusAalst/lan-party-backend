using Mercurius.Modules.Tournament.Application;
using Mercurius.Modules.Tournament.Application.DTOs.Participants;
using Mercurius.Modules.Tournament.Application.DTOs.Registrations;
using Mercurius.Modules.Tournament.Domain;
using Mercurius.Modules.Tournament.Infrastructure;
using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Mercurius.Modules.Teams.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TournamentRosterConfirmationChangedEvent =
    Mercurius.Modules.Tournament.Contracts.TournamentRosterConfirmationChangedEvent;

namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class TournamentRegistrationService : ITournamentRegistrationService
{
    private readonly ITournamentDbContext _dbContext;
    private readonly IIdentityModule _identityModule;
    private readonly ITeamsModule _teamsModule;
    private readonly TournamentEligibilityEvaluator _eligibilityEvaluator;
    private readonly RegistrationMappingContextBuilder _contextBuilder;
    private readonly TournamentRegistrationPersistenceCoordinator _persistenceCoordinator;
    private readonly TournamentRegistrationReadModelService _readModelService;
    private readonly TournamentDtoMapper _mapper;
    private readonly ITournamentRealtimePublisher _realtimePublisher;
    private readonly ILogger<TournamentRegistrationService> _logger;

    public TournamentRegistrationService(
        ITournamentDbContext dbContext,
        IIdentityModule identityModule,
        ITeamsModule teamsModule,
        TournamentEligibilityEvaluator eligibilityEvaluator,
        RegistrationMappingContextBuilder contextBuilder,
        TournamentRegistrationPersistenceCoordinator persistenceCoordinator,
        TournamentRegistrationReadModelService readModelService,
        TournamentDtoMapper mapper,
        ITournamentRealtimePublisher realtimePublisher,
        ILogger<TournamentRegistrationService> logger)
    {
        _dbContext = dbContext;
        _identityModule = identityModule;
        _teamsModule = teamsModule;
        _eligibilityEvaluator = eligibilityEvaluator;
        _contextBuilder = contextBuilder;
        _persistenceCoordinator = persistenceCoordinator;
        _readModelService = readModelService;
        _mapper = mapper;
        _realtimePublisher = realtimePublisher;
        _logger = logger;
    }

    public async Task<EligibilityResponseDTO> CheckIndividualEligibilityAsync(
        string auth0UserId,
        Guid tournamentId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        var reasons = await _eligibilityEvaluator.GetIndividualTournamentFailuresAsync(tournament, userId, null, cancellationToken);
        return new EligibilityResponseDTO(reasons.Count == 0, reasons);
    }

    public async Task<EligibilityResponseDTO> CheckTeamEligibilityAsync(
        string auth0UserId,
        Guid tournamentId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        var team = await GetTeamWithMembersAsync(teamId, cancellationToken);
        var reasons = await GetTeamEligibilityFailuresAsync(tournament, team, userId, null, cancellationToken);
        return new EligibilityResponseDTO(reasons.Count == 0, reasons);
    }

    public async Task<RosterCandidateEligibilityResponseDTO> CheckRosterEligibilityAsync(
        string auth0UserId,
        Guid tournamentId,
        Guid teamId,
        IReadOnlyList<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        var team = await GetTeamWithMembersAsync(teamId, cancellationToken);
        var reasons = await GetTeamEligibilityFailuresAsync(tournament, team, userId, null, cancellationToken);
        reasons.AddRange(TournamentEligibilityEvaluator.GetRosterSizeFailures(tournament, userIds));

        var distinctUserIds = userIds.Distinct().ToList();
        var users = await GetActiveUserProfilesByIdAsync(distinctUserIds, cancellationToken);
        var candidateFailures = await GetRosterCandidateFailuresAsync(tournament.Id, team, distinctUserIds, users, null, cancellationToken);

        var candidateResults = new List<RosterCandidateEligibilityDTO>();
        foreach (var candidateId in distinctUserIds)
        {
            candidateResults.Add(new RosterCandidateEligibilityDTO(
                candidateId,
                users.TryGetValue(candidateId, out var candidate) ? new PublicUserDTO(candidate) : null,
                candidateFailures[candidateId].Count == 0,
                candidateFailures[candidateId]));
        }

        if (candidateResults.Any(candidate => !candidate.Eligible))
            reasons.Add("roster_candidate_ineligible");

        return new RosterCandidateEligibilityResponseDTO(reasons.Count == 0, reasons.Distinct().ToList(), candidateResults);
    }

    public async Task<TournamentRegistrationDTO> RegisterIndividualAsync(
        string auth0UserId,
        Guid tournamentId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        var reasons = await _eligibilityEvaluator.GetIndividualTournamentFailuresAsync(tournament, userId, null, cancellationToken);
        if (reasons.Count != 0)
            throw new ValidationException(string.Join(", ", reasons));
        var userProfile = (await GetActiveUserProfilesByIdAsync([userId], cancellationToken))[userId];

        var now = DateTime.UtcNow;
        var registration = new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Individual,
            Status = TournamentRegistrationStatus.Active,
            RegisteredByUserId = userId,
            RegisteredByUsernameAtRegistration = userProfile.Username ?? string.Empty,
            UserId = userId,
            UsernameAtRegistration = userProfile.Username,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        tournament.IncrementRevision();
        _dbContext.TournamentRegistrations.Add(registration);
        await _persistenceCoordinator.SaveChangesAsync("User already has pending or active participation for this tournament.", cancellationToken);
        var dto = await _mapper.ToRegistrationDtoAsync(
            await GetRegistrationByIdAsync(registration.Id, cancellationToken),
            cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return dto;
    }

    public async Task UnregisterIndividualAsync(
        string auth0UserId,
        Guid tournamentId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        EnsureScheduled(tournament);

        var registration = await _dbContext.TournamentRegistrations
            .FirstOrDefaultAsync(r =>
                r.TournamentId == tournamentId &&
                r.UserId == userId &&
                r.Kind == TournamentRegistrationKind.Individual &&
                r.Status == TournamentRegistrationStatus.Active,
                cancellationToken);
        if (registration is null)
            throw new NotFoundException("Individual registration not found.");

        _dbContext.TournamentRegistrations.Remove(registration);
        await _persistenceCoordinator.SaveChangesAsync("Tournament registration changed concurrently.", cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TournamentRegistrationDTO> SubmitTeamRosterAsync(
        string auth0UserId,
        Guid tournamentId,
        SubmitTeamRosterDTO request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        var team = await GetTeamWithMembersAsync(request.TeamId, cancellationToken);
        var existing = await _dbContext.TournamentRegistrations
            .Include(registration => registration.RosterMembers)
            .FirstOrDefaultAsync(registration =>
                registration.TournamentId == tournamentId &&
                registration.TeamId == team.TeamId.Value &&
                registration.Kind == TournamentRegistrationKind.Team,
                cancellationToken);
        var excludedRegistrationId = existing?.Id;

        var teamFailures = await GetTeamEligibilityFailuresAsync(tournament, team, userId, excludedRegistrationId, cancellationToken);
        var rosterFailures = TournamentEligibilityEvaluator.GetRosterSizeFailures(tournament, request.UserIds);
        if (!request.UserIds.Contains(userId))
            rosterFailures.Add("captain_required");
        var candidateProfiles = await GetActiveUserProfilesByIdAsync(
            request.UserIds.Distinct().ToArray(),
            cancellationToken);
        var candidateFailures = await GetRosterCandidateFailuresAsync(
            tournament.Id,
            team,
            request.UserIds.Distinct().ToList(),
            candidateProfiles,
            excludedRegistrationId,
            cancellationToken);
        foreach (var failure in candidateFailures.Values.SelectMany(static failures => failures))
            rosterFailures.Add(failure);

        var failures = teamFailures.Concat(rosterFailures).Distinct().ToList();
        if (failures.Count != 0)
            throw new ValidationException(string.Join(", ", failures));

        var now = DateTime.UtcNow;
        var registration = existing ?? new TournamentRegistration
        {
            Id = Guid.NewGuid(),
            TournamentId = tournament.Id,
            Kind = TournamentRegistrationKind.Team,
            Status = TournamentRegistrationStatus.PendingConfirmation,
            RegisteredByUserId = userId,
            RegisteredByUsernameAtRegistration = candidateProfiles[userId].Username ?? string.Empty,
            TeamId = team.TeamId.Value,
            TeamNameAtRegistration = team.TeamName,
            TeamCaptainUserIdAtRegistration = team.CaptainUserId?.Value,
            TeamLogoUrlAtRegistration = team.LogoUrl,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var rosterConfirmationEvents = new List<TournamentRosterConfirmationChangedEvent>();

        if (existing is null)
        {
            _dbContext.TournamentRegistrations.Add(registration);
        }
        else
        {
            registration.RegisteredByUserId = userId;
            registration.RegisteredByUsernameAtRegistration = candidateProfiles[userId].Username ?? string.Empty;
            registration.TeamNameAtRegistration = team.TeamName;
            registration.TeamCaptainUserIdAtRegistration = team.CaptainUserId?.Value;
            registration.TeamLogoUrlAtRegistration = team.LogoUrl;

            var requestedUserIds = request.UserIds.ToHashSet();
            var removedMembers = registration.RosterMembers
                .Where(member => !requestedUserIds.Contains(member.UserId))
                .ToList();
            rosterConfirmationEvents.AddRange(removedMembers
                .Where(member => member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending)
                .Select(member => new TournamentRosterConfirmationChangedEvent(
                    registration.TeamId!.Value,
                    member.Id,
                    member.UserId,
                    "Withdrawn")));
            foreach (var removedMember in removedMembers)
                registration.RosterMembers.Remove(removedMember);
        }

        foreach (var memberId in request.UserIds.Distinct())
        {
            var isCaptain = memberId == team.CaptainUserId?.Value;
            var profile = candidateProfiles[memberId];
            var member = registration.RosterMembers.FirstOrDefault(existingMember => existingMember.UserId == memberId);
            if (member is not null)
            {
                member.TeamNameAtRegistration = team.TeamName;
                member.UsernameAtRegistration = profile.Username ?? string.Empty;
                member.DisplayNameAtRegistration = profile.DisplayName;
                member.IsCaptain = isCaptain;
                if (isCaptain && member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending)
                {
                    member.ConfirmationStatus = RosterMemberConfirmationStatus.AutoConfirmed;
                    member.ConfirmedAtUtc = now;
                    member.UpdatedAtUtc = now;
                }
                continue;
            }

            member = new TournamentRegistrationRosterMember
            {
                Id = Guid.NewGuid(),
                TournamentId = tournament.Id,
                TeamId = team.TeamId.Value,
                TeamNameAtRegistration = team.TeamName,
                UserId = memberId,
                UsernameAtRegistration = profile.Username ?? string.Empty,
                DisplayNameAtRegistration = profile.DisplayName,
                IsCaptain = isCaptain,
                ConfirmationStatus = isCaptain ? RosterMemberConfirmationStatus.AutoConfirmed : RosterMemberConfirmationStatus.Pending,
                ConfirmedAtUtc = isCaptain ? now : null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            registration.RosterMembers.Add(member);
            _dbContext.TournamentRegistrationRosterMembers.Add(member);
            if (member.ConfirmationStatus == RosterMemberConfirmationStatus.Pending)
            {
                rosterConfirmationEvents.Add(new TournamentRosterConfirmationChangedEvent(
                    registration.TeamId!.Value,
                    member.Id,
                    member.UserId,
                    nameof(RosterMemberConfirmationStatus.Pending)));
            }
        }

        if (registration.RosterMembers.Count == tournament.TeamSize &&
            registration.RosterMembers.All(member => member.ConfirmationStatus != RosterMemberConfirmationStatus.Pending))
            registration.Activate(now);
        else
        {
            registration.Status = TournamentRegistrationStatus.PendingConfirmation;
            registration.UpdatedAtUtc = now;
        }

        tournament.IncrementRevision();
        await _persistenceCoordinator.SaveChangesAsync("One or more roster members already has pending or active participation for this tournament.", cancellationToken);
        var dto = await _mapper.ToRegistrationDtoAsync(
            await GetRegistrationByIdAsync(registration.Id, cancellationToken),
            cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        await PublishRosterConfirmationEventsAsync(rosterConfirmationEvents, cancellationToken);
        return dto;
    }

    public async Task<TournamentRegistrationDTO> ConfirmRosterAsync(
        string auth0UserId,
        Guid tournamentId,
        Guid rosterMemberId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var member = await _dbContext.TournamentRegistrationRosterMembers
            .Include(roster => roster.TournamentRegistration)
                .ThenInclude(registration => registration.RosterMembers)
            .Include(roster => roster.TournamentRegistration)
                .ThenInclude(registration => registration.Tournament)
            .FirstOrDefaultAsync(
                roster => roster.Id == rosterMemberId && roster.TournamentId == tournamentId && roster.UserId == userId,
                cancellationToken);
        if (member is null || member.ConfirmationStatus != RosterMemberConfirmationStatus.Pending)
            throw new NotFoundException("Pending roster confirmation not found.");

        var registration = member.TournamentRegistration;
        EnsureScheduled(registration.Tournament);
        if (!registration.TeamId.HasValue)
            throw new ValidationException("Team registration is invalid.");
        var team = await GetTeamWithMembersAsync(registration.TeamId.Value, cancellationToken);

        var candidateFailures = await GetRosterCandidateFailuresAsync(
            registration.TournamentId,
            team,
            userId,
            null,
            registration.Id,
            cancellationToken);
        if (candidateFailures.Count != 0)
            throw new ValidationException(string.Join(", ", candidateFailures));

        var now = DateTime.UtcNow;
        member.Confirm(now);
        if (registration.RosterMembers.Count == registration.Tournament.TeamSize &&
            registration.RosterMembers.All(roster => roster.ConfirmationStatus is RosterMemberConfirmationStatus.AutoConfirmed or RosterMemberConfirmationStatus.Confirmed))
            registration.Activate(now);
        else
            registration.UpdatedAtUtc = now;

        await _persistenceCoordinator.SaveChangesAsync("User already has pending or active participation for this tournament.", cancellationToken);
        var dto = await _mapper.ToRegistrationDtoAsync(
            await GetRegistrationByIdAsync(registration.Id, cancellationToken),
            cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        await TryPublishRosterConfirmationChangedAsync(
            registration.TeamId.Value,
            member.Id,
            member.UserId,
            nameof(RosterMemberConfirmationStatus.Confirmed),
            cancellationToken);
        return dto;
    }

    public async Task DeclineRosterAsync(
        string auth0UserId,
        Guid tournamentId,
        Guid rosterMemberId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var member = await _dbContext.TournamentRegistrationRosterMembers
            .Include(roster => roster.TournamentRegistration)
                .ThenInclude(registration => registration.Tournament)
            .FirstOrDefaultAsync(roster =>
                roster.Id == rosterMemberId &&
                roster.TournamentId == tournamentId &&
                roster.UserId == userId &&
                roster.ConfirmationStatus == RosterMemberConfirmationStatus.Pending,
                cancellationToken);

        if (member is null)
            return;

        var registration = member.TournamentRegistration;
        EnsureScheduled(registration.Tournament);
        if (!registration.TeamId.HasValue)
            throw new ValidationException("Team registration is invalid.");

        var now = DateTime.UtcNow;
        registration.Status = TournamentRegistrationStatus.PendingConfirmation;
        registration.UpdatedAtUtc = now;
        _dbContext.TournamentRegistrationRosterMembers.Remove(member);

        await _persistenceCoordinator.SaveChangesAsync("Tournament roster changed while declining the selection.", cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        await TryPublishRosterConfirmationChangedAsync(
            registration.TeamId.Value,
            member.Id,
            member.UserId,
            "Declined",
            cancellationToken);
    }

    public async Task UnregisterTeamAsync(
        string auth0UserId,
        Guid tournamentId,
        Guid teamId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        EnsureScheduled(tournament);
        var team = await GetTeamWithMembersAsync(teamId, cancellationToken);
        EnsureCaptain(team, userId);

        var registration = await GetTeamRegistrationForMutationAsync(tournamentId, teamId, cancellationToken);
        DeleteTransientTeamRegistration(registration);
        await _persistenceCoordinator.SaveChangesAsync("Tournament registration changed concurrently.", cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CurrentUserTournamentRegistrationStateDTO> GetCurrentUserStateAsync(
        string auth0UserId,
        Guid tournamentId,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        var tournament = await GetTournamentAsync(tournamentId, cancellationToken);
        return await _readModelService.GetCurrentUserStateAsync(userId, tournament, cancellationToken);
    }

    public async Task<RosterConfirmationNotificationPageDTO> GetPendingRosterConfirmationsAsync(
        string auth0UserId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var userId = await GetCurrentUserIdAsync(auth0UserId, cancellationToken);
        return await _readModelService.GetPendingRosterConfirmationsAsync(userId, page, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<AdminTournamentRegistrationDTO>> GetAdminRegistrationsAsync(
        Guid tournamentId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        _ = await GetTournamentAsync(tournamentId, cancellationToken);
        return await _readModelService.GetAdminRegistrationsAsync(tournamentId, page, pageSize, cancellationToken);
    }

    public async Task RemoveIndividualAsAdminAsync(
        Guid tournamentId,
        Guid userId,
        string? reason,
        string? adminAuth0UserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        _ = string.IsNullOrWhiteSpace(adminAuth0UserId)
            ? (Guid?)null
            : await GetCurrentUserIdAsync(adminAuth0UserId, cancellationToken);
        var registration = await _dbContext.TournamentRegistrations.FirstOrDefaultAsync(registration =>
            registration.TournamentId == tournamentId &&
            registration.UserId == userId &&
            registration.Kind == TournamentRegistrationKind.Individual,
            cancellationToken);
        if (registration is null)
            throw new NotFoundException("Individual registration not found.");

        _dbContext.TournamentRegistrations.Remove(registration);
        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveTeamAsAdminAsync(
        Guid tournamentId,
        Guid teamId,
        string? reason,
        string? adminAuth0UserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _persistenceCoordinator.BeginTransactionAsync(cancellationToken);
        _ = string.IsNullOrWhiteSpace(adminAuth0UserId)
            ? (Guid?)null
            : await GetCurrentUserIdAsync(adminAuth0UserId, cancellationToken);
        var registration = await GetTeamRegistrationForMutationAsync(tournamentId, teamId, cancellationToken);
        DeleteTransientTeamRegistration(registration);
        await _persistenceCoordinator.SaveChangesAsync("Tournament registration changed concurrently.", cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private async Task<List<string>> GetTeamEligibilityFailuresAsync(
        TournamentAggregate tournament,
        TeamRosterSnapshot team,
        Guid captainUserId,
        Guid? excludedRegistrationId,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        reasons.AddRange(await _eligibilityEvaluator.GetTeamTournamentFailuresAsync(
            tournament,
            team.TeamId.Value,
            captainUserId,
            excludedRegistrationId,
            cancellationToken));
        if (team.IsDeleted)
            reasons.Add("team_deleted");
        if (team.CaptainUserId?.Value != captainUserId)
            reasons.Add("captain_required");
        return reasons;
    }

    private async Task<List<string>> GetRosterCandidateFailuresAsync(
        Guid tournamentId,
        TeamRosterSnapshot team,
        Guid userId,
        IReadOnlyDictionary<Guid, UserProfileSummary>? activeProfiles,
        Guid? excludedRegistrationId,
        CancellationToken cancellationToken)
    {
        var failures = await GetRosterCandidateFailuresAsync(
            tournamentId,
            team,
            [userId],
            activeProfiles,
            excludedRegistrationId,
            cancellationToken);
        return failures[userId];
    }

    private async Task<Dictionary<Guid, List<string>>> GetRosterCandidateFailuresAsync(
        Guid tournamentId,
        TeamRosterSnapshot team,
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyDictionary<Guid, UserProfileSummary>? activeProfiles,
        Guid? excludedRegistrationId,
        CancellationToken cancellationToken)
    {
        var distinctUserIds = userIds.Distinct().ToList();
        if (distinctUserIds.Count == 0)
            return [];

        var activeUserIds = (activeProfiles ?? await GetActiveUserProfilesByIdAsync(distinctUserIds, cancellationToken))
            .Keys
            .ToHashSet();
        var teamMemberIds = team.Members.Select(member => member.UserId.Value).ToHashSet();

        var directParticipationUserIds = await _dbContext.TournamentRegistrations
            .AsNoTracking()
            .Where(registration =>
                registration.TournamentId == tournamentId &&
                registration.UserId.HasValue &&
                distinctUserIds.Contains(registration.UserId.Value) &&
                (!excludedRegistrationId.HasValue || registration.Id != excludedRegistrationId.Value))
            .Select(registration => registration.UserId!.Value)
            .ToListAsync(cancellationToken);

        var rosterParticipationUserIds = await _dbContext.TournamentRegistrationRosterMembers
            .AsNoTracking()
            .Where(member =>
                member.TournamentId == tournamentId &&
                distinctUserIds.Contains(member.UserId) &&
                (!excludedRegistrationId.HasValue || member.TournamentRegistrationId != excludedRegistrationId.Value))
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        var participatingUserIds = directParticipationUserIds
            .Concat(rosterParticipationUserIds)
            .ToHashSet();

        return distinctUserIds.ToDictionary(
            userId => userId,
            userId =>
            {
                var reasons = new List<string>();
                if (!activeUserIds.Contains(userId))
                    reasons.Add("user_not_found");
                if (!teamMemberIds.Contains(userId))
                    reasons.Add("not_team_member");
                if (participatingUserIds.Contains(userId))
                    reasons.Add("duplicate_participation");

                return reasons;
            });
    }

    private async Task PublishRosterConfirmationEventsAsync(
        IEnumerable<TournamentRosterConfirmationChangedEvent> rosterConfirmationEvents,
        CancellationToken cancellationToken)
    {
        foreach (var rosterConfirmationEvent in rosterConfirmationEvents)
        {
            await TryPublishRosterConfirmationChangedAsync(
                rosterConfirmationEvent.TeamId,
                rosterConfirmationEvent.RosterMemberId,
                rosterConfirmationEvent.UserId,
                rosterConfirmationEvent.Status,
                cancellationToken);
        }
    }

    private async Task TryPublishRosterConfirmationChangedAsync(
        Guid teamId,
        Guid rosterMemberId,
        Guid userId,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            await _realtimePublisher.RosterConfirmationChangedAsync(
                teamId,
                rosterMemberId,
                userId,
                status,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Roster confirmation realtime notification failed after persistence for roster member {RosterMemberId} with status {Status}.",
                rosterMemberId,
                status);
        }
    }

    private void DeleteTransientTeamRegistration(TournamentRegistration registration)
    {
        _dbContext.TournamentRegistrations.Remove(registration);
    }

    private async Task<TournamentRegistration> GetTeamRegistrationForMutationAsync(
        Guid tournamentId,
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var registration = await _dbContext.TournamentRegistrations
            .Include(r => r.RosterMembers)
            .FirstOrDefaultAsync(r =>
                r.TournamentId == tournamentId &&
                r.TeamId == teamId &&
                r.Kind == TournamentRegistrationKind.Team,
                cancellationToken);
        if (registration is null)
            throw new NotFoundException("Team registration not found.");
        return registration;
    }

    private Task<TournamentRegistration> GetRegistrationByIdAsync(
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        return GetRegistrationQuery().FirstAsync(
            registration => registration.Id == registrationId,
            cancellationToken);
    }

    private IQueryable<TournamentRegistration> GetRegistrationQuery()
    {
        return _dbContext.TournamentRegistrations
            .Include(registration => registration.RosterMembers)
            ;
    }

    private async Task<TournamentAggregate> GetTournamentAsync(Guid tournamentId, CancellationToken cancellationToken)
    {
        var tournament = await _dbContext.Tournaments.FindAsync([tournamentId], cancellationToken);
        if (tournament is null)
            throw new NotFoundException("Tournament not found");
        if (tournament.BracketType == BracketType.Leaderboard)
            throw new ValidationException("Tournament registration is not available for leaderboard tournaments.");
        return tournament;
    }

    private async Task<TeamRosterSnapshot> GetTeamWithMembersAsync(
        Guid teamId,
        CancellationToken cancellationToken)
    {
        var team = await _teamsModule.GetTeamRosterSnapshotAsync(new TeamId(teamId), cancellationToken);
        if (team is null)
            throw new NotFoundException("Team not found");
        return team;
    }

    private async Task<Guid> GetCurrentUserIdAsync(string auth0UserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(auth0UserId))
            throw new UnauthorizedAccessException("Authenticated user id is missing.");

        var user = await _identityModule.GetUserProfileByAuth0IdAsync(auth0UserId.Trim(), cancellationToken);
        if (user is null || user.IsDeleted)
            throw new NotFoundException("Current user profile was not found.");

        return user.Id.Value;
    }

    private async Task<IReadOnlyDictionary<Guid, UserProfileSummary>> GetActiveUserProfilesByIdAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var profiles = await _identityModule.GetUsersByIdsAsync(
            userIds.Select(userId => new UserId(userId)).ToArray(),
            cancellationToken);

        return profiles.Values
            .Where(user => !user.IsDeleted)
            .ToDictionary(user => user.Id.Value);
    }

    private static void EnsureScheduled(TournamentAggregate tournament)
    {
        if (tournament.Status != TournamentStatus.Scheduled)
            throw new ValidationException("Tournament must be scheduled for registration changes.");
    }

    private static void EnsureCaptain(TeamRosterSnapshot team, Guid userId)
    {
        if (team.CaptainUserId?.Value != userId)
            throw new ForbiddenException("team_captain_required", "Only the team captain can perform this action.");
    }

}
