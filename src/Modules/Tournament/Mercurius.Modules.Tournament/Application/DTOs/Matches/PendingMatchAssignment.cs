namespace Mercurius.Modules.Tournament.Application.DTOs.Matches;

/// <summary>Why a match is in the caller's pending-resolution list. Serialized as a string.</summary>
internal enum PendingMatchAssignment
{
    // The tournament's assigned admin is the caller.
    Assigned = 0,
    // The tournament has no assigned admin.
    Unassigned = 1,
    // The assigned admin is no longer an administrator or no longer exists.
    AssignedAdminUnavailable = 2,
    // The match has waited longer than the escalation wait; every administrator sees it.
    Escalated = 3
}
