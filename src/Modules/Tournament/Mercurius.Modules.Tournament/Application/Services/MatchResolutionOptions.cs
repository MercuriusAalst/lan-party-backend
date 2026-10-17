namespace Mercurius.Modules.Tournament.Application.Services;

internal sealed class MatchResolutionOptions
{
    public const string SectionName = "Tournament:MatchResolution";

    // How long a match may wait for admin resolution before every administrator is shown it.
    public int EscalationAfterMinutes { get; set; } = 10;

    public TimeSpan EscalationAfter => TimeSpan.FromMinutes(EscalationAfterMinutes);
}
