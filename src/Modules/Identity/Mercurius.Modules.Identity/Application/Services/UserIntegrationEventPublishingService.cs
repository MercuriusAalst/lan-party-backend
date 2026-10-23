using Mercurius.Modules.Identity.Contracts;
using Mercurius.Modules.Identity.Application.DTOs;
using Mercurius.Modules.Identity.Infrastructure;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared;
using Mercurius.Modules.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.Runtime.ExceptionServices;
using Platform.Eventing;
using Platform.Realtime;
using Mercurius.Modules.Identity.Application.Services.Auth0;

namespace Mercurius.Modules.Identity.Application.Services;

internal sealed class UserIntegrationEventPublishingService : IUserService
{
    private readonly IUserService _inner;
    private readonly IIdentityDbContext _dbContext;
    private readonly IModuleEventPublisher _moduleEventPublisher;
    private readonly IRealtimeConnectionManager _realtimeConnectionManager;
    private readonly AccountLifecycleService _accountLifecycle;
    private readonly IAuthenticatedIdentityContext _authenticatedIdentityContext;
    private readonly IMediaModule? _mediaModule;
    private readonly ILogger<UserIntegrationEventPublishingService>? _logger;

    public UserIntegrationEventPublishingService(
        IUserService inner,
        IIdentityDbContext dbContext,
        IModuleEventPublisher moduleEventPublisher,
        IRealtimeConnectionManager realtimeConnectionManager,
        AccountLifecycleService accountLifecycle,
        IAuthenticatedIdentityContext authenticatedIdentityContext,
        IMediaModule? mediaModule = null,
        ILogger<UserIntegrationEventPublishingService>? logger = null)
    {
        _inner = inner;
        _dbContext = dbContext;
        _moduleEventPublisher = moduleEventPublisher;
        _realtimeConnectionManager = realtimeConnectionManager;
        _accountLifecycle = accountLifecycle;
        _authenticatedIdentityContext = authenticatedIdentityContext;
        _mediaModule = mediaModule;
        _logger = logger;
    }

    public async Task<GetUserDTO> CreateUserAsync(CreateUserProfileRequest request)
    {
        return await ExecuteProfileMutationAsync(
            () => _inner.CreateUserAsync(request),
            before: null,
            auth0Subject: request.EffectiveAuth0UserId);
    }

    public async Task<GetUserDTO> CreateCurrentUserAsync(string auth0UserId, CompleteUserProfileRequest request)
    {
        if (_inner is UserService userService)
        {
            var auth0Profile = await userService.GetAuth0ProfileForCurrentUserCreationAsync(auth0UserId);

            return await ExecuteProfileMutationAsync(
                () => userService.CreateCurrentUserAsync(auth0UserId, request, auth0Profile),
                before: null,
                auth0Subject: auth0UserId);
        }

        return await ExecuteProfileMutationAsync(
            () => _inner.CreateCurrentUserAsync(auth0UserId, request),
            before: null,
            auth0Subject: auth0UserId);
    }

    public async Task<GetUserDTO> CompleteProfileAsync(string auth0UserId, CompleteUserProfileRequest request)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserByAuth0IdForMutationAsync(auth0UserId);
        var user = await _inner.CompleteProfileAsync(auth0UserId, request);

        await PublishEventsAsync(BuildProfileEvents(user, before));
        await CommitIfStartedAsync(transaction);
        return user;
    }

    public Task<CurrentUserProfileResponse> GetCurrentUserAsync(string auth0UserId)
    {
        return _inner.GetCurrentUserAsync(auth0UserId);
    }

    public Task<PublicUserProfileDTO> GetPublicUserProfileByUsernameAsync(string username)
    {
        return _inner.GetPublicUserProfileByUsernameAsync(username);
    }

    public Task<GetUserDTO> GetUserByUsernameAsync(string username)
    {
        return _inner.GetUserByUsernameAsync(username);
    }

    public Task<UserSearchResponseDTO> SearchUsersAsync(
        string? query,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        return _inner.SearchUsersAsync(query, cursor, pageSize, cancellationToken);
    }

    public async Task<GetUserDTO> UpdateCurrentUserAsync(string auth0UserId, UpdateUserProfileRequest request)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserByAuth0IdForMutationAsync(auth0UserId);
        var user = await _inner.UpdateCurrentUserAsync(auth0UserId, request);

        await PublishEventsAsync(BuildProfileEvents(user, before));
        await CommitIfStartedAsync(transaction);
        return user;
    }

    public async Task<CurrentUserProfileResponse> UploadCurrentUserPictureAsync(string auth0UserId, IFormFile picture, CancellationToken cancellationToken = default)
    {
        var transaction = await BeginTransactionIfSupportedAsync();
        UserEventState? before = null;
        CurrentUserProfileResponse? response = null;
        Exception? failure = null;
        string? failedPictureUrl = null;
        try
        {
            try
            {
                before = await GetUserByAuth0IdForMutationAsync(auth0UserId);
                response = await _inner.UploadCurrentUserPictureAsync(auth0UserId, picture, cancellationToken);
                if (response.User is not null)
                    await PublishEventsAsync(BuildProfileEvents(response.User, before));
                await CommitIfStartedAsync(transaction);
            }
            catch (Exception exception)
            {
                if (transaction is not null)
                    await transaction.RollbackAsync(CancellationToken.None);
                failedPictureUrl = response?.User?.ProfilePictureUrl ??
                    exception.Data[UserService.UncommittedPictureUrlExceptionDataKey] as string;
                failure = exception;
            }
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        if (failure is not null)
        {
            if (before is not null)
            {
                var trackedUser = await _dbContext.Users.FirstOrDefaultAsync(user => user.Id == before.Id);
                if (trackedUser is not null)
                {
                    trackedUser.ProfilePictureUrl = before.ProfilePictureUrl;
                    trackedUser.UpdatedAtUtc = before.UpdatedAtUtc;
                }
            }
            if (!string.IsNullOrWhiteSpace(failedPictureUrl) && !string.Equals(failedPictureUrl, before?.ProfilePictureUrl, StringComparison.Ordinal))
                await DeletePictureIfUnreferencedAsync(failedPictureUrl);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (response?.User is not null)
            await RetirePictureIfUnreferencedAsync(before?.ProfilePictureUrl, response.User.ProfilePictureUrl);
        return response!;
    }

    public async Task<CurrentUserProfileResponse> RemoveCurrentUserPictureAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserByAuth0IdForMutationAsync(auth0UserId, cancellationToken);
        var response = await _inner.RemoveCurrentUserPictureAsync(auth0UserId, cancellationToken);
        if (response.User is not null)
            await PublishEventsAsync(BuildProfileEvents(response.User, before));
        await CommitIfStartedAsync(transaction);
        await RetirePictureIfUnreferencedAsync(before?.ProfilePictureUrl, response.User?.ProfilePictureUrl);
        return response;
    }

    public Task<UsernameAvailabilityResponse> CheckUsernameAvailabilityAsync(string auth0UserId, string username)
    {
        return _inner.CheckUsernameAvailabilityAsync(auth0UserId, username);
    }

    public Task<UserActionResponse> ResendVerificationEmailAsync(string auth0UserId)
    {
        return _inner.ResendVerificationEmailAsync(auth0UserId);
    }

    public Task<UserActionResponse> SendPasswordResetEmailAsync(string auth0UserId)
    {
        return _inner.SendPasswordResetEmailAsync(auth0UserId);
    }

    public async Task<UserActionResponse> AnonymizeCurrentUserAsync(string auth0UserId)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserByAuth0IdForMutationAsync(auth0UserId);
        var response = await _inner.AnonymizeCurrentUserAsync(auth0UserId);
        var after = await GetUserEventStateByIdAsync(before.Id);
        await QueueDeletionCleanupAsync(before, after);
        var deletionEvents = BuildDeletionEvents(before, after);

        await PublishEventsAsync(deletionEvents);
        await CommitIfStartedAsync(transaction);
        try
        {
            if (deletionEvents.Count > 0)
                await _realtimeConnectionManager.RevokeUserAsync(after!.Id, CancellationToken.None);
        }
        finally
        {
            await RetirePictureIfUnreferencedAsync(before?.ProfilePictureUrl, after?.ProfilePictureUrl);
        }
        return response;
    }

    public async Task DeleteUserAsync(string username)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserByNormalizedUsernameForMutationAsync(username);
        if (before is null)
            throw new NotFoundException($"User '{username}' not found.");

        await _inner.DeleteUserByIdAsync(before.Id);
        var after = before is null ? null : await GetUserEventStateByIdAsync(before.Id);
        await QueueDeletionCleanupAsync(before, after);
        var deletionEvents = BuildDeletionEvents(before, after);

        await PublishEventsAsync(deletionEvents);
        await CommitIfStartedAsync(transaction);
        try
        {
            if (deletionEvents.Count > 0)
                await _realtimeConnectionManager.RevokeUserAsync(after!.Id, CancellationToken.None);
        }
        finally
        {
            await RetirePictureIfUnreferencedAsync(before?.ProfilePictureUrl, after?.ProfilePictureUrl);
        }
    }

    public async Task DeleteUserByIdAsync(Guid id)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        var before = await GetUserEventStateByIdAsync(id);
        if (before is not null)
        {
            await UserMutationLock.AcquireSubjectAsync(_dbContext, before.Auth0UserId);
            await UserMutationLock.AcquireAsync(_dbContext, id);
            before = await GetUserEventStateByIdAsync(id);
        }
        await _inner.DeleteUserByIdAsync(id);
        var after = before is null ? null : await GetUserEventStateByIdAsync(before.Id);
        await QueueDeletionCleanupAsync(before, after);
        var deletionEvents = BuildDeletionEvents(before, after);

        await PublishEventsAsync(deletionEvents);
        await CommitIfStartedAsync(transaction);
        try
        {
            if (deletionEvents.Count > 0)
                await _realtimeConnectionManager.RevokeUserAsync(after!.Id, CancellationToken.None);
        }
        finally
        {
            await RetirePictureIfUnreferencedAsync(before?.ProfilePictureUrl, after?.ProfilePictureUrl);
        }
    }

    public Task<IReadOnlyList<GetUserDTO>> GetAllUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        return _inner.GetAllUsersAsync(page, pageSize, cancellationToken);
    }

    public Task<GetUserDTO> GetUserByIdAsync(Guid id)
    {
        return _inner.GetUserByIdAsync(id);
    }

    public Task<IReadOnlyList<AdminUserOptionDTO>> GetAdminUsersAsync(
        string? query,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        return _inner.GetAdminUsersAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<GetUserDTO> UpdateUserAsync(Guid id, UpdateUserProfileRequest request)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        await UserMutationLock.AcquireAsync(_dbContext, id);
        var before = await GetUserEventStateByIdAsync(id);
        var user = await _inner.UpdateUserAsync(id, request);

        await PublishEventsAsync(BuildProfileEvents(user, before));
        await CommitIfStartedAsync(transaction);
        return user;
    }

    private async Task<GetUserDTO> ExecuteProfileMutationAsync(
        Func<Task<GetUserDTO>> mutation,
        UserEventState? before,
        string? auth0Subject = null)
    {
        await using var transaction = await BeginTransactionIfSupportedAsync();
        if (!string.IsNullOrWhiteSpace(auth0Subject))
        {
            auth0Subject = auth0Subject.Trim();
            await UserMutationLock.AcquireSubjectAsync(_dbContext, auth0Subject);
            await _accountLifecycle.EnsureSubjectCanRegisterAsync(
                auth0Subject,
                _authenticatedIdentityContext.IssuedAtUnixSeconds,
                string.Equals(_authenticatedIdentityContext.Subject, auth0Subject, StringComparison.Ordinal));
        }
        var user = await mutation();

        await PublishEventsAsync(BuildProfileEvents(user, before));
        await CommitIfStartedAsync(transaction);
        return user;
    }

    private async Task PublishEventsAsync(IReadOnlyCollection<object> events)
    {
        if (events.Count == 0)
            return;

        foreach (var integrationEvent in events)
            _moduleEventPublisher.Publish(integrationEvent);

        await _dbContext.SaveChangesAsync();
    }

    private static IReadOnlyCollection<object> BuildProfileEvents(GetUserDTO user, UserEventState? before)
    {
        var events = new List<object>
        {
            new UserProfileChangedIntegrationEvent(
                new UserId(user.Id),
                user.Username,
                user.DisplayName,
                user.IsDeleted,
                IsSearchable(user),
                user.UpdatedAtUtc,
                user.ProfilePictureUrl)
        };

        if (UsernameChanged(before, user) && !string.IsNullOrWhiteSpace(user.Username))
        {
            events.Add(new UsernameChangedIntegrationEvent(
                new UserId(user.Id),
                user.Username,
                user.UpdatedAtUtc));
        }

        return events;
    }

    private static IReadOnlyCollection<object> BuildDeletionEvents(UserEventState? before, UserEventState? after)
    {
        if (before is null || before.IsDeleted || after is null || !after.IsDeleted || after.DeletedAtUtc is null)
            return [];

        var events = new List<object>
        {
            new UserDeletedIntegrationEvent(new UserId(after.Id), after.DeletedAtUtc.Value),
            new UserProfileChangedIntegrationEvent(
                new UserId(after.Id),
                after.Username,
                after.DisplayName,
                after.IsDeleted,
                IsSearchable(after),
                after.UpdatedAtUtc,
                after.ProfilePictureUrl)
        };

        if (!string.IsNullOrWhiteSpace(after.Username))
        {
            events.Add(new UsernameChangedIntegrationEvent(
                new UserId(after.Id),
                after.Username,
                after.UpdatedAtUtc));
        }

        return events;
    }

    private static bool UsernameChanged(UserEventState? before, GetUserDTO after)
    {
        return before is null ||
            !string.Equals(before.Username, after.Username, StringComparison.Ordinal);
    }

    private static bool IsSearchable(GetUserDTO user)
    {
        return !user.IsDeleted &&
            !string.IsNullOrWhiteSpace(user.Username) &&
            !string.IsNullOrWhiteSpace(user.Firstname) &&
            !string.IsNullOrWhiteSpace(user.Lastname);
    }

    private static bool IsSearchable(UserEventState user)
    {
        return !user.IsDeleted &&
            !string.IsNullOrWhiteSpace(user.Username) &&
            !string.Equals(user.DisplayName, "Incomplete profile", StringComparison.Ordinal);
    }

    private async Task<UserEventState?> GetUserByAuth0IdForMutationAsync(string auth0UserId, CancellationToken cancellationToken = default)
    {
        var normalizedAuth0UserId = auth0UserId.Trim();
        await UserMutationLock.AcquireSubjectAsync(_dbContext, normalizedAuth0UserId, cancellationToken);
        var userId = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Auth0UserId == normalizedAuth0UserId)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is null)
            return null;
        await UserMutationLock.AcquireAsync(_dbContext, userId.Value, cancellationToken);
        return await GetUserEventStateByIdAsync(userId.Value);
    }

    private async Task<UserEventState?> GetUserByNormalizedUsernameForMutationAsync(string username)
    {
        var normalizedUsername = UserProfileValidationHelper.NormalizeUsername(username);
        var state = await _dbContext.Users
            .AsNoTracking()
            .Where(user => user.NormalizedUsername == normalizedUsername)
            .Select(user => new { user.Id, user.Auth0UserId })
            .FirstOrDefaultAsync();
        if (state is null)
            return null;
        await UserMutationLock.AcquireSubjectAsync(_dbContext, state.Auth0UserId);
        await UserMutationLock.AcquireAsync(_dbContext, state.Id);
        return await GetUserEventStateByIdAsync(state.Id);
    }

    private Task<UserEventState?> GetUserEventStateByIdAsync(Guid id)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == id)
            .Select(user => new UserEventState(
                user.Id,
                user.Auth0UserId,
                user.Username,
                user.DisplayName,
                user.IsDeleted,
                user.DeletedAtUtc,
                user.UpdatedAtUtc,
                user.ProfilePictureUrl))
            .FirstOrDefaultAsync();
    }

    private async Task QueueDeletionCleanupAsync(UserEventState? before, UserEventState? after)
    {
        if (before is null || before.IsDeleted || after is null || !after.IsDeleted)
            return;
        await _accountLifecycle.QueueCleanupAsync(
            before.Auth0UserId,
            before.Id,
            after.DeletedAtUtc ?? after.UpdatedAtUtc);
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync()
    {
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            return null;
        }

        return await _dbContext.Database.BeginTransactionAsync();
    }

    private static async Task CommitIfStartedAsync(IDbContextTransaction? transaction)
    {
        if (transaction is not null)
            await transaction.CommitAsync();
    }

    private async Task RetirePictureIfUnreferencedAsync(string? url, string? currentUrl)
    {
        if (string.IsNullOrWhiteSpace(url) || string.Equals(url, currentUrl, StringComparison.Ordinal))
            return;
        await DeletePictureIfUnreferencedAsync(url);
    }

    private async Task DeletePictureIfUnreferencedAsync(string url)
    {
        try
        {
            if (await _dbContext.Users.AsNoTracking().AnyAsync(user => user.ProfilePictureUrl == url))
                return;
            await (_mediaModule ?? throw new InvalidOperationException("Media module is unavailable."))
                .DeleteImageAsync(url, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Failed to safely retire user picture at {MediaUrl}; the file was retained.", url);
        }
    }

    private sealed record UserEventState(
        Guid Id,
        string Auth0UserId,
        string? Username,
        string DisplayName,
        bool IsDeleted,
        DateTime? DeletedAtUtc,
        DateTime UpdatedAtUtc,
        string? ProfilePictureUrl);
}
