namespace Mercurius.Modules.Tournament.Contracts;

// Values are persisted in outbox payloads as numbers: only append, never renumber.
public enum MatchResolutionReason
{
    Disputed = 0,
    MissingOpponentReport = 1
}
