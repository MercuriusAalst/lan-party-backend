# Retire unused match update route and consumerless events

## Why

`PUT /v1/lan/matches/{id}` duplicates `POST /v1/lan/matches/{id}/resolve`: both call the same administrative score resolution with the same validation, authorization and result recording. The frontend binds the route in its API client but no page or component calls it, so keeping two routes for one command only widens the admin surface. Likewise, two integration events are written to the outbox on every account deletion or sponsor placement change but no consumer handles them.

## What Changes

- Remove the admin-only `PUT /v1/lan/matches/{id}` route without a compatibility alias. `POST /v1/lan/matches/{id}/resolve` remains the single administrative score-resolution route with unchanged request, response and authorization.
- Stop publishing `UserAnonymizedIntegrationEvent` and `Contracts.V1.TournamentSponsorPlacementChanged`; no handler, projection or realtime path consumes them (account deletion still publishes `UserDeletedIntegrationEvent` and `UserProfileChangedIntegrationEvent`). Already stored outbox rows of these types are acknowledged as retired.

## Impact

- Route contract: one admin route removed; it is tested as absent from the endpoint table and the OpenAPI document.
- Frontend: the unused `ILANClient.UpdateMatchAsync` / `ITournamentService.UpdateMatchScoresAsync` bindings should be removed in the frontend.
- Eventing: two event contracts removed; their stored outbox rows are marked processed by the dispatcher instead of dead-lettered.
- No DTO, JSON shape, persistence or schema change for the remaining routes.
