using System.Text.Json.Serialization;

namespace Mercurius.Modules.Discovery.Contracts;

public sealed record DiscoverySearchResult(
    string Type,
    string DisplayLabel,
    string SupportingText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Username,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TeamName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Guid? TournamentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ProfilePictureUrl = null);
