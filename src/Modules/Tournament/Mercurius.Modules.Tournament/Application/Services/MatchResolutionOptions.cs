namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class MatchResolutionOptions
{
    public const string SectionName = "Tournament:MatchResolution";

    // How long a match may wait for admin resolution before every administrator is shown it.
    public int EscalationAfterMinutes { get; set; } = 10;

    // Upper bound for the whole batch of live "is this assigned admin still an administrator" lookups per request.
    // Not bound from configuration.
    internal TimeSpan AdminLookupTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan EscalationAfter => TimeSpan.FromMinutes(EscalationAfterMinutes);
}
