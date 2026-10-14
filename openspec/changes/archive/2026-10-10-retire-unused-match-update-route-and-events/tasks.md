## Backend implementation

- [x] Remove the `PUT /v1/lan/matches/{id}` endpoint, its service method and its request DTO.
- [x] Test that the route is absent from the endpoint table and that the OpenAPI document no longer exposes `PUT` on `/v1/lan/matches/{id}`.
- [x] Stop publishing `UserAnonymizedIntegrationEvent` and `TournamentSponsorPlacementChanged`, delete their contracts, and register both names as retired so stored rows are acknowledged.
- [x] Synchronize the live specs and archive this change.

## Follow-up (frontend repository)

- Remove the unused `ILANClient.UpdateMatchAsync` / `ITournamentService.UpdateMatchScoresAsync` bindings.
