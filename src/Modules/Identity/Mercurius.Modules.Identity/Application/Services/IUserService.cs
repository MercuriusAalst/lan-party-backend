using Mercurius.Modules.Identity.Application.DTOs;
using Microsoft.AspNetCore.Http;

namespace Mercurius.Modules.Identity.Application.Services;

internal interface IUserService
{
    Task<GetUserDTO> CreateUserAsync(CreateUserProfileRequest request);
    Task<GetUserDTO> CreateCurrentUserAsync(string auth0UserId, CompleteUserProfileRequest request);
    Task<GetUserDTO> CompleteProfileAsync(string auth0UserId, CompleteUserProfileRequest request);
    Task<CurrentUserProfileResponse> GetCurrentUserAsync(string auth0UserId);
    Task<PublicUserProfileDTO> GetPublicUserProfileByUsernameAsync(string username);
    Task<GetUserDTO> GetUserByUsernameAsync(string username);
    Task<UserSearchResponseDTO> SearchUsersAsync(string? query, string? cursor, int pageSize, CancellationToken cancellationToken = default);
    Task<GetUserDTO> UpdateCurrentUserAsync(string auth0UserId, UpdateUserProfileRequest request);
    Task<CurrentUserProfileResponse> UploadCurrentUserPictureAsync(string auth0UserId, IFormFile picture, CancellationToken cancellationToken = default);
    Task<CurrentUserProfileResponse> RemoveCurrentUserPictureAsync(string auth0UserId, CancellationToken cancellationToken = default);
    Task<UsernameAvailabilityResponse> CheckUsernameAvailabilityAsync(string auth0UserId, string username);
    Task<UserActionResponse> ResendVerificationEmailAsync(string auth0UserId);
    Task<UserActionResponse> SendPasswordResetEmailAsync(string auth0UserId);
    Task<UserActionResponse> AnonymizeCurrentUserAsync(string auth0UserId);
    Task DeleteUserAsync(string username);
    Task DeleteUserByIdAsync(Guid id);
    Task<IReadOnlyList<GetUserDTO>> GetAllUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<GetUserDTO> GetUserByIdAsync(Guid id);
    Task<GetUserDTO> UpdateUserAsync(Guid id, UpdateUserProfileRequest request);
    Task<IReadOnlyList<AdminUserOptionDTO>> GetAdminUsersAsync(string? query, int? page, int? pageSize, CancellationToken cancellationToken = default);
}
