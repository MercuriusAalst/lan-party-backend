namespace Mercurius.Modules.Identity.Application.DTOs;

internal class CurrentUserProfileResponse
{
    public bool IsComplete { get; set; }
    public GetUserDTO? User { get; set; }
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }

    public CurrentUserProfileResponse(bool isComplete, GetUserDTO? user)
    {
        IsComplete = isComplete;
        User = user;
        Email = user?.Email;
        EmailVerified = user?.EmailVerified ?? false;
    }
}
