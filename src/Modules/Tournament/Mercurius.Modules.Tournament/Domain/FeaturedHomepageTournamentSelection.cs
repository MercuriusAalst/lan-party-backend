namespace Mercurius.Modules.Tournament.Domain;

internal sealed class FeaturedHomepageTournamentSelection
{
    public const int SingletonId = 1;

    public int Id { get; private set; } = SingletonId;
    public Guid[] TournamentIds { get; private set; } = [];
}
