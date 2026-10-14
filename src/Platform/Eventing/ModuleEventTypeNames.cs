namespace Platform.Eventing;

internal static class ModuleEventTypeNames
{
    private static readonly IReadOnlyDictionary<string, string> LegacyTypeAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The rename migration rewrites pending rows. These aliases are retained only for
            // historical/dead-lettered or rollback rows that still carry the former CLR names.
            ["Mercurius.Modules.Competition.Contracts.GameCreatedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentCreatedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameUpdatedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentUpdatedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameStartedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentStartedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameResetIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentResetIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameCompletedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentCompletedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameCanceledIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentCanceledIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.GameDeletedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentDeletedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.MatchCompletedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.MatchCompletedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.PlacementAssignedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.PlacementAssignedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.RosterMemberConfirmedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.RosterMemberConfirmedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.TournamentRegistrationCanceledIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentRegistrationCanceledIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.TournamentRegistrationCreatedIntegrationEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentRegistrationCreatedIntegrationEvent",
            ["Mercurius.Modules.Competition.Contracts.TournamentRosterConfirmationChangedEvent"] = "Mercurius.Modules.Tournament.Contracts.TournamentRosterConfirmationChangedEvent",
            ["Mercurius.Modules.Sponsorship.Contracts.V1.GameSponsorPlacementChanged"] = "Mercurius.Modules.Sponsorship.Contracts.V1.TournamentSponsorPlacementChanged"
        };

    // Events removed because nothing consumed them. Rows that were already stored are
    // acknowledged without dispatch instead of failing until they are dead-lettered.
    private static readonly HashSet<string> RetiredTypeNames = new(StringComparer.Ordinal)
    {
        "Mercurius.Modules.Identity.Contracts.UserAnonymizedIntegrationEvent",
        "Mercurius.Modules.Sponsorship.Contracts.V1.TournamentSponsorPlacementChanged",
        "Mercurius.Modules.Teams.Contracts.TeamCaptainTransferredIntegrationEvent",
        "Mercurius.Modules.Teams.Contracts.TeamMemberAddedIntegrationEvent",
        "Mercurius.Modules.Teams.Contracts.TeamMemberRemovedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.MatchCompletedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.MatchResultReversedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.PlacementAssignedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.RosterMemberConfirmedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.TournamentCompletedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.TournamentRegistrationCanceledIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.TournamentRegistrationCreatedIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.TournamentResetIntegrationEvent",
        "Mercurius.Modules.Tournament.Contracts.TournamentStartedIntegrationEvent"
    };

    public static string GetName(Type eventType)
    {
        // Internal module events use their CLR full name as the durable type key,
        // keeping event contracts free from platform attributes or registration.
        return eventType.FullName
            ?? throw new InvalidOperationException($"Module event type '{eventType.Name}' must have a full name.");
    }

    public static Type Resolve(string eventTypeName)
    {
        var typeName = StripAssemblyQualification(eventTypeName);
        if (LegacyTypeAliases.TryGetValue(typeName, out var currentTypeName))
            typeName = currentTypeName;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var eventType = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (eventType is not null)
                return eventType;
        }

        throw new InvalidOperationException($"Module event type '{eventTypeName}' could not be resolved.");
    }

    public static bool IsRetired(string eventTypeName)
    {
        var typeName = StripAssemblyQualification(eventTypeName);
        return RetiredTypeNames.Contains(LegacyTypeAliases.GetValueOrDefault(typeName, typeName));
    }

    public static bool IsLegacy(string eventTypeName) =>
        LegacyTypeAliases.ContainsKey(StripAssemblyQualification(eventTypeName));

    private static string StripAssemblyQualification(string eventTypeName)
    {
        var separatorIndex = eventTypeName.IndexOf(',', StringComparison.Ordinal);
        return (separatorIndex < 0 ? eventTypeName : eventTypeName[..separatorIndex]).Trim();
    }
}
