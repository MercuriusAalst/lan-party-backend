using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Tournament.Domain;

internal sealed class Match
{
    public Guid Id { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime EndTime { get; private set; }
    public DateTime? EstimatedStartTime { get; private set; }
    public DateTime? EstimatedEndTime { get; private set; }
    public BracketType BracketType { get; private set; }
    public GameFormat Format { get; private set; }
    public ParticipationMode ParticipationMode { get; private set; }
    public int RoundNumber { get; private set; }
    public int MatchNumber { get; private set; }
    public bool IsLowerBracketMatch { get; private set; }
    public Guid TournamentId { get; private set; }
    public Guid? UserParticipant1Id { get; private set; }
    public Guid? UserParticipant2Id { get; private set; }
    public Guid? UserWinnerId { get; private set; }
    public Guid? UserLoserId { get; private set; }
    public Guid? TeamParticipant1Id { get; private set; }
    public Guid? TeamParticipant2Id { get; private set; }
    public Guid? TeamWinnerId { get; private set; }
    public Guid? TeamLoserId { get; private set; }
    public int? Participant1Score { get; private set; }
    public int? Participant2Score { get; private set; }
    public Guid? WinnerNextMatchId { get; private set; }
    public Guid? LoserNextMatchId { get; private set; }
    // A populated downstream slot is reversible only when its source edge is known.
    // Null means that the slot was part of the original bracket assignment.
    public Guid? Participant1SourceMatchId { get; private set; }
    public Guid? Participant2SourceMatchId { get; private set; }
    public bool Participant1IsBYE { get; private set; }
    public bool Participant2IsBYE { get; private set; }
    public MatchLifecycleState LifecycleState { get; private set; } = MatchLifecycleState.AwaitingEndedConfirmation;
    public DateTime? Participant1EndedConfirmedAtUtc { get; private set; }
    public DateTime? Participant2EndedConfirmedAtUtc { get; private set; }
    public int? Participant1ReportedScore1 { get; private set; }
    public int? Participant1ReportedScore2 { get; private set; }
    public int? Participant2ReportedScore1 { get; private set; }
    public int? Participant2ReportedScore2 { get; private set; }
    public DateTime? ScoreConfirmationDeadlineUtc { get; private set; }
    public DateTime? CorrectionDeadlineUtc { get; private set; }
    public int Participant1CorrectionCount { get; private set; }
    public int Participant2CorrectionCount { get; private set; }
    public int? ForfeitedParticipantNumber { get; private set; }
    public MatchResultKind? ResultKind { get; private set; }
    public Guid? ResultRecordedByUserId { get; private set; }
    public DateTime? ResultRecordedAtUtc { get; private set; }
    public int ResultVersion { get; private set; }
    public TournamentAggregate Tournament { get; private set; } = null!;
    public Match? WinnerNextMatch { get; private set; }
    public Match? LoserNextMatch { get; private set; }

    public Match()
    {
    }

    public Match(
        Guid tournamentId,
        int roundNumber,
        int matchNumber,
        BracketType bracketType,
        GameFormat format,
        ParticipationMode participationMode,
        bool isLowerBracketMatch = false)
    {
        TournamentId = tournamentId;
        RoundNumber = roundNumber;
        MatchNumber = matchNumber;
        BracketType = bracketType;
        Format = format;
        ParticipationMode = participationMode;
        IsLowerBracketMatch = isLowerBracketMatch;
    }

    public bool Participant1Ended => Participant1EndedConfirmedAtUtc.HasValue;
    public bool Participant2Ended => Participant2EndedConfirmedAtUtc.HasValue;
    public bool HasBothParticipants => HasParticipant1() && HasParticipant2();
    public bool HasResult => LifecycleState is MatchLifecycleState.Completed or MatchLifecycleState.Forfeited;

    public bool ConfirmEnded(int participantNumber, DateTime nowUtc)
    {
        EnsureParticipantNumber(participantNumber);
        EnsureOpenForParticipantAction();

        if (Participant1IsBYE || Participant2IsBYE)
            throw new ValidationException("A BYE match cannot accept participant actions.");

        if (participantNumber == 1)
        {
            if (Participant1Ended)
                throw new ValidationException("Participant 1 has already confirmed that the match ended.");
            if (!HasParticipant1())
                throw new ValidationException("Participant 1 is not assigned to this match.");
            Participant1EndedConfirmedAtUtc = nowUtc;
        }
        else
        {
            if (Participant2Ended)
                throw new ValidationException("Participant 2 has already confirmed that the match ended.");
            if (!HasParticipant2())
                throw new ValidationException("Participant 2 is not assigned to this match.");
            Participant2EndedConfirmedAtUtc = nowUtc;
        }

        if (Participant1Ended && Participant2Ended)
            LifecycleState = MatchLifecycleState.AwaitingScore;

        ResultVersion++;
        return Participant1Ended && Participant2Ended;
    }

    public bool SubmitScore(
        int participantNumber,
        int participant1Score,
        int participant2Score,
        DateTime nowUtc)
    {
        EnsureParticipantNumber(participantNumber);
        ValidateDecisiveScore(participant1Score, participant2Score);
        ApplyDeadline(nowUtc);

        if (!HasBothParticipants || Participant1IsBYE || Participant2IsBYE)
            throw new ValidationException("Both non-BYE participants must be assigned before submitting a score.");
        if (!Participant1Ended || !Participant2Ended)
            throw new ValidationException("Both participants must confirm the match ended before submitting a score.");
        if (LifecycleState is MatchLifecycleState.Completed or MatchLifecycleState.Forfeited)
            throw new ValidationException("The match already has a final result.");
        if (LifecycleState == MatchLifecycleState.AdminResolutionRequired)
            throw new ValidationException("This match requires an administrator to resolve the result.");
        if (LifecycleState == MatchLifecycleState.ScoreConfirmation &&
            ((participantNumber == 1 && Participant1ReportedScore1.HasValue) ||
             (participantNumber == 2 && Participant2ReportedScore1.HasValue)))
            throw new ValidationException("Your initial score report has already been submitted.");
        if (LifecycleState == MatchLifecycleState.Disputed &&
            CorrectionDeadlineUtc.HasValue &&
            nowUtc >= CorrectionDeadlineUtc.Value)
        {
            ApplyDeadline(nowUtc);
            throw new ValidationException("The correction window has expired; an administrator must resolve this match.");
        }

        var isCorrection = LifecycleState == MatchLifecycleState.Disputed;
        if (participantNumber == 1)
        {
            if (isCorrection && Participant1CorrectionCount >= 1)
                throw new ValidationException("Participant 1 has already used their correction.");
            Participant1ReportedScore1 = participant1Score;
            Participant1ReportedScore2 = participant2Score;
            if (isCorrection)
                Participant1CorrectionCount++;
        }
        else
        {
            if (isCorrection && Participant2CorrectionCount >= 1)
                throw new ValidationException("Participant 2 has already used their correction.");
            Participant2ReportedScore1 = participant1Score;
            Participant2ReportedScore2 = participant2Score;
            if (isCorrection)
                Participant2CorrectionCount++;
        }

        ResultVersion++;

        if (LifecycleState == MatchLifecycleState.AwaitingScore)
        {
            ScoreConfirmationDeadlineUtc = nowUtc.AddMinutes(5);
            LifecycleState = MatchLifecycleState.ScoreConfirmation;
        }
        else if (Participant1ReportedScore1.HasValue && Participant2ReportedScore1.HasValue)
        {
            if (ReportedScoresMatch())
                CompleteScore(participant1Score, participant2Score, nowUtc, MatchResultKind.Score);
            else
            {
                ScoreConfirmationDeadlineUtc = null;
                CorrectionDeadlineUtc = nowUtc.AddMinutes(5);
                LifecycleState = MatchLifecycleState.Disputed;
            }
        }

        return HasResult;
    }

    public bool ResolveScore(int participant1Score, int participant2Score, DateTime nowUtc)
    {
        ValidateDecisiveScore(participant1Score, participant2Score);
        ApplyDeadline(nowUtc);
        if (LifecycleState is not (MatchLifecycleState.Disputed or MatchLifecycleState.AdminResolutionRequired))
            throw new ValidationException("Only a disputed match can be resolved by an administrator.");
        if (!HasBothParticipants || Participant1IsBYE || Participant2IsBYE)
            throw new ValidationException("Both non-BYE participants must be assigned before resolving this match.");

        CompleteScore(participant1Score, participant2Score, nowUtc, MatchResultKind.AdminResolution);
        return true;
    }

    public bool Forfeit(int participantNumber, DateTime nowUtc)
    {
        EnsureParticipantNumber(participantNumber);
        if (LifecycleState is MatchLifecycleState.Completed or MatchLifecycleState.Forfeited)
            throw new ValidationException("The match already has a final result.");
        if (LifecycleState == MatchLifecycleState.AdminResolutionRequired)
            throw new ValidationException("This match requires an administrator to resolve the result.");
        if (!HasBothParticipants || Participant1IsBYE || Participant2IsBYE)
            throw new ValidationException("A match must have two assigned non-BYE participants before it can be forfeited.");

        ForfeitedParticipantNumber = participantNumber;
        if (participantNumber == 1)
        {
            Participant1Score = 0;
            Participant2Score = 1;
            SetWinnerAndLoser(GetParticipant2Id(), GetParticipant1Id());
        }
        else
        {
            Participant1Score = 1;
            Participant2Score = 0;
            SetWinnerAndLoser(GetParticipant1Id(), GetParticipant2Id());
        }

        ScoreConfirmationDeadlineUtc = null;
        CorrectionDeadlineUtc = null;
        LifecycleState = MatchLifecycleState.Forfeited;
        ResultKind = MatchResultKind.Forfeit;
        ResultRecordedAtUtc = nowUtc;
        ResultVersion++;
        EndTime = nowUtc;
        UpdateParticipantsNextMatch();
        return true;
    }

    public void ApplyDeadline(DateTime nowUtc)
    {
        if (LifecycleState == MatchLifecycleState.ScoreConfirmation &&
            ScoreConfirmationDeadlineUtc is { } scoreDeadline &&
            nowUtc >= scoreDeadline)
        {
            if (Participant1ReportedScore1.HasValue && Participant2ReportedScore1.HasValue)
            {
                if (ReportedScoresMatch())
                    CompleteScore(Participant1ReportedScore1.Value, Participant1ReportedScore2!.Value, nowUtc, MatchResultKind.Score);
                else
                {
                    ScoreConfirmationDeadlineUtc = null;
                    CorrectionDeadlineUtc = nowUtc.AddMinutes(5);
                    LifecycleState = MatchLifecycleState.Disputed;
                    ResultVersion++;
                }
            }
            else if (Participant1ReportedScore1.HasValue || Participant2ReportedScore1.HasValue)
            {
                var score1 = Participant1ReportedScore1 ?? Participant2ReportedScore1!.Value;
                var score2 = Participant1ReportedScore2 ?? Participant2ReportedScore2!.Value;
                CompleteScore(score1, score2, nowUtc, MatchResultKind.Score);
            }
        }

        if (LifecycleState == MatchLifecycleState.Disputed &&
            CorrectionDeadlineUtc is { } correctionDeadline &&
            nowUtc >= correctionDeadline)
        {
            LifecycleState = MatchLifecycleState.AdminResolutionRequired;
            CorrectionDeadlineUtc = null;
            ResultVersion++;
        }
    }

    public void ReverseResult(DateTime nowUtc)
    {
        if (LifecycleState is not (MatchLifecycleState.Completed or MatchLifecycleState.Forfeited))
            throw new ValidationException("Only a completed or forfeited match can be reversed.");

        Participant1Score = null;
        Participant2Score = null;
        UserWinnerId = null;
        UserLoserId = null;
        TeamWinnerId = null;
        TeamLoserId = null;
        EndTime = default;
        Participant1EndedConfirmedAtUtc = null;
        Participant2EndedConfirmedAtUtc = null;
        Participant1ReportedScore1 = null;
        Participant1ReportedScore2 = null;
        Participant2ReportedScore1 = null;
        Participant2ReportedScore2 = null;
        ScoreConfirmationDeadlineUtc = null;
        CorrectionDeadlineUtc = null;
        Participant1CorrectionCount = 0;
        Participant2CorrectionCount = 0;
        ForfeitedParticipantNumber = null;
        ResultKind = null;
        ResultRecordedByUserId = null;
        ResultRecordedAtUtc = nowUtc;
        LifecycleState = MatchLifecycleState.Reversed;
        ResultVersion++;
    }

    public void ForceCompleteScore(int participant1Score, int participant2Score, DateTime nowUtc)
    {
        ValidateDecisiveScore(participant1Score, participant2Score);
        CompleteScore(participant1Score, participant2Score, nowUtc, MatchResultKind.AdminResolution);
    }

    public void SetIndividualParticipants(Guid? participant1Id, Guid? participant2Id)
    {
        EnsureParticipationMode(ParticipationMode.Individual);
        var changed = UserParticipant1Id != participant1Id || UserParticipant2Id != participant2Id ||
            TeamParticipant1Id.HasValue || TeamParticipant2Id.HasValue ||
            Participant1SourceMatchId.HasValue || Participant2SourceMatchId.HasValue;
        ClearTeamAssignments();
        UserParticipant1Id = participant1Id;
        UserParticipant2Id = participant2Id;
        Participant1SourceMatchId = null;
        Participant2SourceMatchId = null;
        if (changed)
            ResultVersion++;
    }

    public void SetTeamParticipants(Guid? participant1Id, Guid? participant2Id)
    {
        EnsureParticipationMode(ParticipationMode.Team);
        var changed = TeamParticipant1Id != participant1Id || TeamParticipant2Id != participant2Id ||
            UserParticipant1Id.HasValue || UserParticipant2Id.HasValue ||
            Participant1SourceMatchId.HasValue || Participant2SourceMatchId.HasValue;
        ClearUserAssignments();
        TeamParticipant1Id = participant1Id;
        TeamParticipant2Id = participant2Id;
        Participant1SourceMatchId = null;
        Participant2SourceMatchId = null;
        if (changed)
            ResultVersion++;
    }

    public void TryAssignByeWin()
    {
        if (!(Participant1IsBYE || Participant2IsBYE))
            return;

        if (!HasParticipant1() && HasParticipant2())
            AssignWinner(GetParticipant2Id());
        else if (!HasParticipant2() && HasParticipant1())
            AssignWinner(GetParticipant1Id());
    }

    public void SetParticipantBYEs(bool participant1BYE, bool participant2BYE)
    {
        if (RoundNumber != 1 && participant1BYE && participant2BYE)
            return;

        var previousParticipant1IsBYE = Participant1IsBYE;
        var previousParticipant2IsBYE = Participant2IsBYE;
        Participant1IsBYE |= participant1BYE;
        Participant2IsBYE |= participant2BYE;
        if (previousParticipant1IsBYE != Participant1IsBYE || previousParticipant2IsBYE != Participant2IsBYE)
            ResultVersion++;
    }

    public void Finish(DateTime nowUtc)
    {
        EndTime = nowUtc;
        LifecycleState = MatchLifecycleState.Completed;
        ResultKind = MatchResultKind.Score;
        ResultRecordedAtUtc = EndTime;
        ResultVersion++;
        UpdateParticipantsNextMatch();
    }

    public void SetWinnerNextMatch(Match? match) => WinnerNextMatch = match;

    public void SetLoserNextMatch(Match? match) => LoserNextMatch = match;

    public void RecordResultBy(Guid userId) => ResultRecordedByUserId = userId;

    // A lower bracket slot that no live feed can ever reach; unlike SetParticipantBYEs it applies in any round.
    public void MarkBothParticipantsAsBYE()
    {
        Participant1IsBYE = true;
        Participant2IsBYE = true;
    }

    public void SetEstimatedWindow(DateTime estimatedStartTime, DateTime estimatedEndTime)
    {
        if (estimatedEndTime <= estimatedStartTime)
            throw new ValidationException("Estimated match end time must be greater than estimated start time.");

        EstimatedStartTime = estimatedStartTime;
        EstimatedEndTime = estimatedEndTime;
    }

    public void SetScoresAndWinner(int participant1Score, int participant2Score, DateTime nowUtc)
    {
        if (participant1Score < 0 || participant2Score < 0)
            throw new ValidationException("Scores cannot be negative");

        var winsNeeded = Format switch
        {
            GameFormat.BestOf1 => 1,
            GameFormat.BestOf3 => 2,
            GameFormat.BestOf5 => 3,
            _ => 1
        };

        if (participant1Score > winsNeeded || participant2Score > winsNeeded)
            throw new ValidationException("Scores cannot exceed the required number of wins for the match format.");
        if (participant1Score == participant2Score && participant1Score + participant2Score != 0 && winsNeeded == 1)
            throw new ValidationException("Scores cannot be equal in Bo1 format");

        Participant1Score = participant1Score;
        Participant2Score = participant2Score;

        if (participant1Score == winsNeeded && participant1Score > participant2Score)
        {
            SetWinnerAndLoser(GetParticipant1Id(), GetParticipant2Id());
            Finish(nowUtc);
        }
        else if (participant2Score == winsNeeded && participant2Score > participant1Score)
        {
            SetWinnerAndLoser(GetParticipant2Id(), GetParticipant1Id());
            Finish(nowUtc);
        }
        else
        {
            LifecycleState = MatchLifecycleState.AwaitingScore;
            ResultVersion++;
        }
    }

    public void UpdateParticipantsNextMatch()
    {
        if (!HasWinner())
            return;

        if (WinnerNextMatch is not null)
        {
            if (WinnerNextMatch.IsLowerBracketMatch)
            {
                if (!WinnerNextMatch.HasParticipant2() || WinnerNextMatch.GetParticipant2Id() == GetWinnerId())
                    AssignWinnerToParticipant2(WinnerNextMatch);
                else
                    AssignWinnerToParticipant1(WinnerNextMatch);

                if (WinnerNextMatch.Participant1IsBYE || WinnerNextMatch.Participant2IsBYE)
                    WinnerNextMatch.TryAssignByeWin();
            }
            else if (MatchNumber % 2 != 0 && !IsLowerBracketMatch)
            {
                AssignWinnerToParticipant1(WinnerNextMatch);
            }
            else
            {
                AssignWinnerToParticipant2(WinnerNextMatch);
            }
        }

        if (LoserNextMatch is null)
            return;

        if (RoundNumber == 1 && MatchNumber % 2 == 0)
            AssignLoserToParticipant2(LoserNextMatch);
        else
            AssignLoserToParticipant1(LoserNextMatch);

        if (LoserNextMatch.Participant1IsBYE || LoserNextMatch.Participant2IsBYE)
            LoserNextMatch.TryAssignByeWin();
    }

    public void SetIndividualParticipant1(Guid? participantId)
    {
        EnsureParticipationMode(ParticipationMode.Individual);
        SetParticipant1(participantId, null);
    }

    public void SetIndividualParticipant2(Guid? participantId)
    {
        EnsureParticipationMode(ParticipationMode.Individual);
        SetParticipant2(participantId, null);
    }

    public void SetTeamParticipant1(Guid? participantId)
    {
        EnsureParticipationMode(ParticipationMode.Team);
        SetParticipant1(participantId, null);
    }

    public void SetTeamParticipant2(Guid? participantId)
    {
        EnsureParticipationMode(ParticipationMode.Team);
        SetParticipant2(participantId, null);
    }

    public bool HasParticipant1() => GetParticipant1Id().HasValue;

    public bool HasParticipant2() => GetParticipant2Id().HasValue;

    public bool HasWinner() => GetWinnerId().HasValue;

    public Guid? GetParticipant1Id()
    {
        return ParticipationMode == ParticipationMode.Individual
            ? UserParticipant1Id
            : TeamParticipant1Id;
    }

    public Guid? GetParticipant2Id()
    {
        return ParticipationMode == ParticipationMode.Individual
            ? UserParticipant2Id
            : TeamParticipant2Id;
    }

    public Guid? GetWinnerId()
    {
        return ParticipationMode == ParticipationMode.Individual
            ? UserWinnerId
            : TeamWinnerId;
    }

    private void AssignWinner(Guid? winnerId)
    {
        if (ParticipationMode == ParticipationMode.Individual)
        {
            ClearTeamAssignments();
            UserWinnerId = winnerId;
            UserLoserId = null;
        }
        else
        {
            ClearUserAssignments();
            TeamWinnerId = winnerId;
            TeamLoserId = null;
        }

        UpdateParticipantsNextMatch();
    }

    public Guid? GetLoserForMutation() => GetLoserId();

    public void ClearParticipant(Guid participantId)
    {
        var changed = false;
        if (ParticipationMode == ParticipationMode.Individual)
        {
            if (UserParticipant1Id == participantId)
            {
                UserParticipant1Id = null;
                Participant1IsBYE = false;
                Participant1SourceMatchId = null;
                changed = true;
            }
            if (UserParticipant2Id == participantId)
            {
                UserParticipant2Id = null;
                Participant2IsBYE = false;
                Participant2SourceMatchId = null;
                changed = true;
            }
        }
        else
        {
            if (TeamParticipant1Id == participantId)
            {
                TeamParticipant1Id = null;
                Participant1IsBYE = false;
                Participant1SourceMatchId = null;
                changed = true;
            }
            if (TeamParticipant2Id == participantId)
            {
                TeamParticipant2Id = null;
                Participant2IsBYE = false;
                Participant2SourceMatchId = null;
                changed = true;
            }
        }

        if (changed)
            ResultVersion++;
    }

    public void ClearParticipantFromSource(Guid sourceMatchId)
    {
        var participant1Changed = Participant1SourceMatchId == sourceMatchId;
        var participant2Changed = Participant2SourceMatchId == sourceMatchId;
        if (!participant1Changed && !participant2Changed)
            return;

        var participant1WasBYE = Participant1IsBYE;
        var participant2WasBYE = Participant2IsBYE;

        if (participant1Changed)
        {
            SetParticipant1(null, null);
            Participant1IsBYE = false;
        }

        if (participant2Changed)
        {
            SetParticipant2(null, null);
            Participant2IsBYE = false;
        }

        if ((participant1Changed && participant1WasBYE) || (participant2Changed && participant2WasBYE))
            ResultVersion++;
    }

    private void CompleteScore(
        int participant1Score,
        int participant2Score,
        DateTime nowUtc,
        MatchResultKind resultKind)
    {
        Participant1Score = participant1Score;
        Participant2Score = participant2Score;
        if (participant1Score > participant2Score)
            SetWinnerAndLoser(GetParticipant1Id(), GetParticipant2Id());
        else
            SetWinnerAndLoser(GetParticipant2Id(), GetParticipant1Id());

        ScoreConfirmationDeadlineUtc = null;
        CorrectionDeadlineUtc = null;
        LifecycleState = MatchLifecycleState.Completed;
        ResultKind = resultKind;
        ResultRecordedAtUtc = nowUtc;
        ResultVersion++;
        EndTime = nowUtc;
        UpdateParticipantsNextMatch();
    }

    private void ValidateDecisiveScore(int participant1Score, int participant2Score)
    {
        if (participant1Score < 0 || participant2Score < 0)
            throw new ValidationException("Scores cannot be negative.");

        var winsNeeded = GetWinsNeeded();
        if (participant1Score > winsNeeded || participant2Score > winsNeeded)
            throw new ValidationException("Scores cannot exceed the required number of wins for the match format.");
        if (participant1Score == participant2Score ||
            (participant1Score != winsNeeded && participant2Score != winsNeeded))
            throw new ValidationException("A final score must have one participant reach the required number of wins.");
    }

    private int GetWinsNeeded() =>
        Format switch
        {
            GameFormat.BestOf1 => 1,
            GameFormat.BestOf3 => 2,
            GameFormat.BestOf5 => 3,
            _ => 1
        };

    private bool ReportedScoresMatch() =>
        Participant1ReportedScore1 == Participant2ReportedScore1 &&
        Participant1ReportedScore2 == Participant2ReportedScore2;

    private void EnsureOpenForParticipantAction()
    {
        if (LifecycleState is MatchLifecycleState.Completed or
            MatchLifecycleState.Forfeited or
            MatchLifecycleState.AdminResolutionRequired)
            throw new ValidationException("This match is not accepting participant actions.");
    }

    private static void EnsureParticipantNumber(int participantNumber)
    {
        if (participantNumber is not (1 or 2))
            throw new ValidationException("Participant number must be 1 or 2.");
    }

    private void SetWinnerAndLoser(Guid? winnerId, Guid? loserId)
    {
        if (ParticipationMode == ParticipationMode.Individual)
        {
            ClearTeamAssignments();
            UserWinnerId = winnerId;
            UserLoserId = loserId;
        }
        else
        {
            ClearUserAssignments();
            TeamWinnerId = winnerId;
            TeamLoserId = loserId;
        }
    }

    // A bracket that is still being generated has no ids yet; its bye advancement is part of the original assignment.
    private Guid? SourceMatchId => Id == Guid.Empty ? null : Id;

    private void AssignWinnerToParticipant1(Match targetMatch) =>
        targetMatch.SetParticipant1(GetWinnerId(), SourceMatchId);

    private void AssignWinnerToParticipant2(Match targetMatch) =>
        targetMatch.SetParticipant2(GetWinnerId(), SourceMatchId);

    private void AssignLoserToParticipant1(Match targetMatch) =>
        targetMatch.SetParticipant1(GetLoserId(), SourceMatchId);

    private void AssignLoserToParticipant2(Match targetMatch) =>
        targetMatch.SetParticipant2(GetLoserId(), SourceMatchId);

    private void SetParticipant1(Guid? participantId, Guid? sourceMatchId)
    {
        if (ParticipationMode == ParticipationMode.Individual)
        {
            ClearTeamAssignments();
            if (UserParticipant1Id == participantId && Participant1SourceMatchId == sourceMatchId)
                return;

            UserParticipant1Id = participantId;
            Participant1SourceMatchId = sourceMatchId;
        }
        else
        {
            ClearUserAssignments();
            if (TeamParticipant1Id == participantId && Participant1SourceMatchId == sourceMatchId)
                return;

            TeamParticipant1Id = participantId;
            Participant1SourceMatchId = sourceMatchId;
        }

        ResultVersion++;
    }

    private void SetParticipant2(Guid? participantId, Guid? sourceMatchId)
    {
        if (ParticipationMode == ParticipationMode.Individual)
        {
            ClearTeamAssignments();
            if (UserParticipant2Id == participantId && Participant2SourceMatchId == sourceMatchId)
                return;

            UserParticipant2Id = participantId;
            Participant2SourceMatchId = sourceMatchId;
        }
        else
        {
            ClearUserAssignments();
            if (TeamParticipant2Id == participantId && Participant2SourceMatchId == sourceMatchId)
                return;

            TeamParticipant2Id = participantId;
            Participant2SourceMatchId = sourceMatchId;
        }

        ResultVersion++;
    }

    private Guid? GetLoserId()
    {
        return ParticipationMode == ParticipationMode.Individual
            ? UserLoserId
            : TeamLoserId;
    }

    private void EnsureParticipationMode(ParticipationMode expectedMode)
    {
        if (ParticipationMode != expectedMode)
            throw new ValidationException($"Match only accepts {expectedMode.ToString().ToLowerInvariant()} participants.");
    }

    private void ClearUserAssignments()
    {
        UserParticipant1Id = null;
        UserParticipant2Id = null;
        UserWinnerId = null;
        UserLoserId = null;
    }

    private void ClearTeamAssignments()
    {
        TeamParticipant1Id = null;
        TeamParticipant2Id = null;
        TeamWinnerId = null;
        TeamLoserId = null;
    }
}
