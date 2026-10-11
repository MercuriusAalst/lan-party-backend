using Mercurius.Modules.Shared.Exceptions;
using LeaderboardMetric = Mercurius.Modules.Tournament.Domain.LeaderboardRankingMetric;

namespace Mercurius.Modules.Tournament.Domain;

internal sealed class Tournament
{
    private const int MaxAverageGameDurationMinutes = 1440;
    internal const int MaximumTeamSize = 50;

    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public DateTime PlannedStartTime { get; set; }
    public int AverageGameDurationMinutes { get; set; }
    public int RoundBreakDurationMinutes { get; set; }
    public DateTime? EstimatedEndTime { get; set; }
    public TournamentStatus Status { get; set; }
    public BracketType BracketType { get; set; }
    public LeaderboardRankingMetric? LeaderboardRankingMetric { get; set; }
    public GameFormat Format { get; set; }
    public GameFormat FinalsFormat { get; set; }
    public ParticipationMode ParticipationMode { get; set; }
    public int? TeamSize { get; set; }
    public Guid? AssignedAdminUserId { get; set; }
    public string? FirstPlacePrize { get; set; }
    public string? SecondPlacePrize { get; set; }
    public string? ThirdPlacePrize { get; set; }
    public IList<Placement> Placements { get; set; } = [];
    public IList<Match> Matches { get; set; } = [];
    public IList<TournamentRegistration> TournamentRegistrations { get; set; } = [];
    public IList<LeaderboardParticipant> LeaderboardParticipants { get; set; } = [];
    /// <summary>
    /// Shared optimistic-concurrency revision for the tournament aggregate, including configuration, lifecycle, registration, and leaderboard mutations.
    /// The legacy property and database column name do not limit the scope of the token.
    /// </summary>
    public long LeaderboardRevision { get; set; }
    public string? ImageUrl { get; set; }

    public Tournament(
        string name,
        BracketType bracketType,
        GameFormat format,
        GameFormat finalsFormat,
        ParticipationMode participationMode,
        int? teamSize,
        DateTime plannedStartTime,
        int averageGameDurationMinutes,
        int roundBreakDurationMinutes,
        LeaderboardRankingMetric? leaderboardRankingMetric = null)
    {
        Name = name;
        BracketType = bracketType;
        Format = format;
        FinalsFormat = finalsFormat;
        Status = TournamentStatus.Scheduled;
        ParticipationMode = participationMode;
        SetLeaderboardConfiguration(bracketType, leaderboardRankingMetric, participationMode);
        SetTeamSize(teamSize);
        SetScheduleConfiguration(plannedStartTime, averageGameDurationMinutes, roundBreakDurationMinutes);
    }

    public Tournament(
        string name,
        BracketType bracketType,
        GameFormat format,
        GameFormat finalsFormat,
        ParticipationMode participationMode,
        int? teamSize = null,
        LeaderboardRankingMetric? leaderboardRankingMetric = null)
        : this(name, bracketType, format, finalsFormat, participationMode, teamSize, DateTime.UtcNow, 30, 10, leaderboardRankingMetric)
    {
    }

    public Tournament()
    {
    }

    public void Update(
        string name,
        BracketType bracketType,
        GameFormat format,
        GameFormat finalsFormat,
        ParticipationMode participationMode,
        int? teamSize,
        DateTime plannedStartTime,
        int averageGameDurationMinutes,
        int roundBreakDurationMinutes,
        LeaderboardRankingMetric? leaderboardRankingMetric = null)
    {
        if (Status is TournamentStatus.InProgress or TournamentStatus.Completed)
            throw new ValidationException("Tournament cannot be updated when it's in progress or completed.");
        if (ParticipationMode != participationMode && (Matches.Count != 0 || HasRegistrations()))
            throw new ValidationException("Participation mode cannot be changed once registration or match generation has started.");
        if (Matches.Count != 0 && ScheduleConfigurationChanged(plannedStartTime, averageGameDurationMinutes, roundBreakDurationMinutes))
            throw new ValidationException("Schedule configuration cannot be changed once match generation has started.");
        if (TeamSizeChanged(teamSize) && (Matches.Count != 0 || HasRegistrations()))
            throw new ValidationException("Team size cannot be changed once registration or match generation has started.");
        if (BracketType != bracketType && (Matches.Count != 0 || HasRegistrations() || LeaderboardParticipants.Count != 0))
            throw new ValidationException("Bracket type cannot be changed once tournament participation has started.");
        if (LeaderboardRankingMetric != leaderboardRankingMetric && Status != TournamentStatus.Scheduled)
            throw new ValidationException("Leaderboard ranking metric cannot be changed after the tournament has started.");

        Name = name;
        BracketType = bracketType;
        Format = format;
        FinalsFormat = finalsFormat;
        ParticipationMode = participationMode;
        SetLeaderboardConfiguration(bracketType, leaderboardRankingMetric, participationMode);
        SetTeamSize(teamSize);
        SetScheduleConfiguration(plannedStartTime, averageGameDurationMinutes, roundBreakDurationMinutes);
    }

    public void Cancel()
    {
        if (Status == TournamentStatus.Completed)
            throw new ValidationException("Tournament cannot be canceled when it's already completed.");
        Status = TournamentStatus.Canceled;
    }

    public void Start()
    {
        if (Status != TournamentStatus.Scheduled)
            throw new ValidationException("Tournament has to be scheduled to be able to start");
        if (BracketType != BracketType.Leaderboard && GetRegisteredParticipantCount() < 2)
            throw new ValidationException("At least 2 participants required.");

        StartTime = DateTime.UtcNow;
        Status = TournamentStatus.InProgress;
    }

    public void Complete()
    {
        if (Status != TournamentStatus.InProgress)
            throw new ValidationException("Tournament has to be in progress to be able to complete");

        EndTime = DateTime.UtcNow;
        Status = TournamentStatus.Completed;
    }

    public void Reset()
    {
        if (Status is not (TournamentStatus.Completed or TournamentStatus.Canceled))
            throw new ValidationException("Tournament has to be completed or canceled to be able to reset");

        Status = TournamentStatus.Scheduled;
        StartTime = DateTime.MinValue;
        EndTime = DateTime.MinValue;
        EstimatedEndTime = null;
        Matches.Clear();
        Placements.Clear();
        LeaderboardParticipants.Clear();
        LeaderboardRevision++;
    }

    public int GetRegisteredParticipantCount()
    {
        var activeRegistrations = TournamentRegistrations
            .Where(registration => registration.Status == TournamentRegistrationStatus.Active);

        return ParticipationMode switch
        {
            ParticipationMode.Individual => activeRegistrations.Count(registration => registration.Kind == TournamentRegistrationKind.Individual),
            ParticipationMode.Team => activeRegistrations.Count(registration => registration.Kind == TournamentRegistrationKind.Team),
            _ => 0
        };
    }

    public IReadOnlyList<Guid> GetActiveRegisteredUserIds()
    {
        return TournamentRegistrations
            .Where(registration =>
                registration.Kind == TournamentRegistrationKind.Individual &&
                registration.Status == TournamentRegistrationStatus.Active &&
                registration.UserId.HasValue)
            .Select(registration => registration.UserId!.Value)
            .ToList();
    }

    public IReadOnlyList<Guid> GetActiveRegisteredTeamIds()
    {
        return TournamentRegistrations
            .Where(registration =>
                registration.Kind == TournamentRegistrationKind.Team &&
                registration.Status == TournamentRegistrationStatus.Active &&
                registration.TeamId.HasValue)
            .Select(registration => registration.TeamId!.Value)
            .ToList();
    }

    public LeaderboardParticipant FindLeaderboardParticipant(Guid participantId) =>
        LeaderboardParticipants.SingleOrDefault(participant => participant.Id == participantId)
        ?? throw new NotFoundException("Leaderboard participant not found.");

    public LeaderboardParticipant? FindLeaderboardParticipantByLinkedUserId(Guid linkedUserId) =>
        LeaderboardParticipants.SingleOrDefault(participant => participant.LinkedUserId == linkedUserId);

    public LeaderboardParticipant AddGuestLeaderboardParticipant(string displayName)
    {
        var participant = new LeaderboardParticipant
        {
            Id = Guid.NewGuid(),
            TournamentId = Id,
            DisplayName = displayName.Trim()
        };
        LeaderboardParticipants.Add(participant);
        return participant;
    }

    public LeaderboardParticipant AddLinkedLeaderboardParticipant(Guid linkedUserId, string displayName)
    {
        var participant = new LeaderboardParticipant
        {
            Id = Guid.NewGuid(),
            TournamentId = Id,
            LinkedUserId = linkedUserId,
            DisplayName = displayName
        };
        LeaderboardParticipants.Add(participant);
        return participant;
    }

    public LeaderboardParticipant? ValidateCanRecordLeaderboardAttempt(
        Guid? participantId,
        Guid? linkedUserId,
        string? guestDisplayName,
        decimal? score,
        long? durationMilliseconds)
    {
        EnsureLeaderboardAttemptsEditable();
        ValidateLeaderboardAttemptValue(score, durationMilliseconds);
        ValidateLeaderboardAttemptSelector(participantId, linkedUserId, guestDisplayName);

        if (participantId.HasValue)
            return FindLeaderboardParticipant(participantId.Value);
        if (linkedUserId.HasValue)
            return FindLeaderboardParticipantByLinkedUserId(linkedUserId.Value);
        return null;
    }

    public (LeaderboardParticipant Participant, LeaderboardAttempt Attempt) RecordLeaderboardAttempt(
        Guid? participantId,
        Guid? linkedUserId,
        string? guestDisplayName,
        string? linkedUserDisplayName,
        decimal? score,
        long? durationMilliseconds,
        DateTime nowUtc)
    {
        var existingParticipant = ValidateCanRecordLeaderboardAttempt(
            participantId,
            linkedUserId,
            guestDisplayName,
            score,
            durationMilliseconds);
        var participant = existingParticipant ?? (linkedUserId.HasValue
            ? AddLinkedLeaderboardParticipant(
                linkedUserId.Value,
                linkedUserDisplayName ?? throw new ValidationException("Linked user display name is required."))
            : AddGuestLeaderboardParticipant(guestDisplayName!));
        var attempt = participant.AddAttempt(score, durationMilliseconds, nowUtc);
        return (participant, attempt);
    }

    public LeaderboardAttempt UpdateLeaderboardAttempt(
        Guid attemptId,
        Guid rowVersion,
        decimal? score,
        long? durationMilliseconds,
        DateTime nowUtc)
    {
        EnsureLeaderboardAttemptsEditable();
        ValidateLeaderboardAttemptValue(score, durationMilliseconds);
        var attempt = FindLeaderboardAttempt(attemptId);
        attempt.EnsureRowVersion(rowVersion);
        attempt.Correct(score, durationMilliseconds, nowUtc);
        return attempt;
    }

    public void RemoveLeaderboardAttempt(Guid attemptId, Guid rowVersion)
    {
        EnsureLeaderboardAttemptsEditable();
        var participant = LeaderboardParticipants
            .SingleOrDefault(item => item.Attempts.Any(attempt => attempt.Id == attemptId))
            ?? throw new NotFoundException("Leaderboard attempt not found.");
        var attempt = participant.Attempts.Single(item => item.Id == attemptId);
        attempt.EnsureRowVersion(rowVersion);
        participant.Attempts.Remove(attempt);
    }

    private void EnsureLeaderboardAttemptsEditable()
    {
        if (Status != TournamentStatus.InProgress)
            throw new ValidationException("Leaderboard attempts can only be changed while the tournament is in progress.");
    }

    private void ValidateLeaderboardAttemptValue(decimal? score, long? durationMilliseconds)
    {
        if (!LeaderboardRankingMetric.HasValue)
            throw new ValidationException("Tournament has no supported leaderboard ranking metric.");
        LeaderboardRankingMetric.Value.ValidateAttemptValue(score, durationMilliseconds);
    }

    private static void ValidateLeaderboardAttemptSelector(
        Guid? participantId,
        Guid? linkedUserId,
        string? guestDisplayName)
    {
        var count = (participantId.HasValue ? 1 : 0)
            + (linkedUserId.HasValue ? 1 : 0)
            + (!string.IsNullOrWhiteSpace(guestDisplayName) ? 1 : 0);
        if (count != 1)
            throw new ValidationException("Exactly one of participantId, linkedUserId, or guestDisplayName is required.");
    }

    public IReadOnlyList<LeaderboardRankingEntry> GetLeaderboardRanking()
    {
        if (!LeaderboardRankingMetric.HasValue)
            return [];

        var metric = LeaderboardRankingMetric.Value;
        var candidates = LeaderboardParticipants
            .Select(participant => new
            {
                Participant = participant,
                Score = participant.BestScore,
                Duration = participant.BestDurationMilliseconds
            })
            .Where(item => metric == LeaderboardMetric.HighestScore ? item.Score.HasValue : item.Duration.HasValue);
        var ordered = metric == LeaderboardMetric.HighestScore
            ? candidates.OrderByDescending(item => item.Score).ThenBy(item => item.Participant.Id).ToList()
            : candidates.OrderBy(item => item.Duration).ThenBy(item => item.Participant.Id).ToList();

        var ranking = new List<LeaderboardRankingEntry>(ordered.Count);
        decimal? previousScore = null;
        long? previousDuration = null;
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var tied = index > 0 && (metric == LeaderboardMetric.HighestScore
                ? item.Score == previousScore
                : item.Duration == previousDuration);
            ranking.Add(new LeaderboardRankingEntry(
                item.Participant,
                item.Score,
                item.Duration,
                tied ? ranking[^1].Rank : index + 1));
            previousScore = item.Score;
            previousDuration = item.Duration;
        }
        return ranking;
    }

    private LeaderboardAttempt FindLeaderboardAttempt(Guid attemptId) =>
        LeaderboardParticipants
            .SelectMany(participant => participant.Attempts)
            .SingleOrDefault(attempt => attempt.Id == attemptId)
        ?? throw new NotFoundException("Leaderboard attempt not found.");

    private void SetScheduleConfiguration(
        DateTime plannedStartTime,
        int averageGameDurationMinutes,
        int roundBreakDurationMinutes)
    {
        if (plannedStartTime == DateTime.MinValue)
            throw new ValidationException("Planned tournament start time is required.");
        if (BracketType == BracketType.Leaderboard)
        {
            PlannedStartTime = plannedStartTime;
            AverageGameDurationMinutes = 0;
            RoundBreakDurationMinutes = 0;
            return;
        }
        if (averageGameDurationMinutes <= 0)
            throw new ValidationException("Average tournament duration must be greater than zero.");
        if (averageGameDurationMinutes > MaxAverageGameDurationMinutes)
            throw new ValidationException($"Average tournament duration cannot exceed {MaxAverageGameDurationMinutes} minutes.");
        if (roundBreakDurationMinutes <= 0)
            throw new ValidationException("Round break duration must be greater than zero.");

        PlannedStartTime = plannedStartTime;
        AverageGameDurationMinutes = averageGameDurationMinutes;
        RoundBreakDurationMinutes = roundBreakDurationMinutes;
    }

    private void SetTeamSize(int? teamSize)
    {
        if (ParticipationMode == ParticipationMode.Team)
        {
            if (!teamSize.HasValue || teamSize.Value <= 0)
                throw new ValidationException("Team tournaments require a team size greater than zero.");
            if (teamSize.Value > MaximumTeamSize)
                throw new ValidationException($"Team tournament size cannot exceed {MaximumTeamSize}.");

            TeamSize = teamSize.Value;
            return;
        }

        TeamSize = null;
    }

    private void SetLeaderboardConfiguration(
        BracketType bracketType,
        LeaderboardRankingMetric? leaderboardRankingMetric,
        ParticipationMode participationMode)
    {
        if (bracketType == BracketType.Leaderboard)
        {
            if (participationMode != ParticipationMode.Individual)
                throw new ValidationException("Leaderboard tournaments must use individual participation.");
            if (!leaderboardRankingMetric.HasValue || !Enum.IsDefined(leaderboardRankingMetric.Value))
                throw new ValidationException("Leaderboard tournaments require a supported ranking metric.");
            LeaderboardRankingMetric = leaderboardRankingMetric;
            return;
        }

        if (leaderboardRankingMetric.HasValue)
            throw new ValidationException("Ranking metric is only supported for leaderboard tournaments.");
        LeaderboardRankingMetric = null;
    }

    private bool TeamSizeChanged(int? teamSize)
    {
        var normalizedTeamSize = ParticipationMode == ParticipationMode.Team ? teamSize : null;
        return TeamSize != normalizedTeamSize;
    }

    private bool HasRegistrations() => TournamentRegistrations.Count != 0;

    private bool ScheduleConfigurationChanged(
        DateTime plannedStartTime,
        int averageGameDurationMinutes,
        int roundBreakDurationMinutes)
    {
        return PlannedStartTime != plannedStartTime
               || AverageGameDurationMinutes != averageGameDurationMinutes
               || RoundBreakDurationMinutes != roundBreakDurationMinutes;
    }
}
