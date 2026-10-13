namespace Mercurius.Modules.Identity.Application.DTOs;

internal sealed class UserSearchResultDTO
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Username { get; init; }
    public required string DisplayLabel { get; init; }
    public string? SupportingText { get; init; }
}
