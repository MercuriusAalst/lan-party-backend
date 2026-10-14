using Mercurius.Modules.Tournament.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Mercurius.Modules.Tournament.Infrastructure;

internal interface ITournamentDbContext
{
    DbSet<TournamentAggregate> Tournaments { get; }
    DbSet<FeaturedHomepageTournamentSelection> FeaturedHomepageTournamentSelections { get; }
    DbSet<Match> Matches { get; }
    DbSet<Placement> Placements { get; }
    DbSet<MatchResolutionNotification> MatchResolutionNotifications { get; }
    DbSet<TournamentRegistration> TournamentRegistrations { get; }
    DbSet<TournamentRegistrationRosterMember> TournamentRegistrationRosterMembers { get; }
    DbSet<LeaderboardParticipant> LeaderboardParticipants { get; }
    DbSet<LeaderboardAttempt> LeaderboardAttempts { get; }
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
