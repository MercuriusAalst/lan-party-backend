using System.Collections.Concurrent;

namespace Mercurius.Modules.Tournament.Application.Services;

/// <summary>
/// Short-lived per-process cache of "is this assigned admin still an administrator". The lookup behind it is a live
/// Auth0 management call and the pending-resolution list is polled by every open admin tab, so repeated polls must not
/// each reach Auth0. It only bounds cost; losing it (restart, other replica) merely repeats a lookup.
/// </summary>
internal sealed class AssignedAdminAvailabilityCache
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, (bool IsAvailable, DateTime ExpiresAtUtc)> _entries = new();

    public AssignedAdminAvailabilityCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryGet(Guid adminUserId, out bool isAvailable)
    {
        if (_entries.TryGetValue(adminUserId, out var entry) && entry.ExpiresAtUtc > _timeProvider.GetUtcNow().UtcDateTime)
        {
            isAvailable = entry.IsAvailable;
            return true;
        }

        isAvailable = false;
        return false;
    }

    public void Set(Guid adminUserId, bool isAvailable)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (var expired in _entries.Where(entry => entry.Value.ExpiresAtUtc <= nowUtc).Select(entry => entry.Key))
            _entries.TryRemove(expired, out _);

        _entries[adminUserId] = (isAvailable, nowUtc.Add(TimeToLive));
    }
}
