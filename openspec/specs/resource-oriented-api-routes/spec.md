# resource-oriented-api-routes Specification

## Purpose

Define the resource-oriented v1 route for protected tournament lifecycle transitions and the id constraints on match and tournament routes.

## Requirements

### Requirement: Tournament lifecycle state resource
The API MUST expose an admin-authorized `PUT /v1/lan/tournaments/{tournamentId}/lifecycle-state` endpoint. The request MUST set `state` to one of `Scheduled`, `InProgress`, `Completed`, or `Canceled`, and the API MUST apply the existing tournament lifecycle operation for that state. The former `/v1/lan/games/{gameId}/lifecycle-state` route MUST NOT be exposed.

#### Scenario: Administrator starts a tournament through its lifecycle-state resource
- **WHEN** an administrator sends `PUT /v1/lan/tournaments/{tournamentId}/lifecycle-state` with `state` set to `InProgress`
- **THEN** the API MUST apply the existing tournament-start rules and return the existing successful start response

#### Scenario: Administrator completes a tournament through its lifecycle-state resource
- **WHEN** an administrator sends `PUT /v1/lan/tournaments/{tournamentId}/lifecycle-state` with `state` set to `Completed`
- **THEN** the API MUST apply the existing completion rules and return the existing placement response JSON shape

#### Scenario: Administrator resets or cancels a tournament through its lifecycle-state resource
- **WHEN** an administrator sends `PUT /v1/lan/tournaments/{tournamentId}/lifecycle-state` with `state` set to `Scheduled` or `Canceled`
- **THEN** the API MUST apply the existing reset or cancellation rules respectively and retain the corresponding successful response behavior

#### Scenario: Unsupported lifecycle state is rejected
- **WHEN** a client sends a lifecycle-state request with a value other than `Scheduled`, `InProgress`, `Completed`, or `Canceled`
- **THEN** the API MUST reject the request without changing tournament state

#### Scenario: Tournament lifecycle action routes are absent
- **WHEN** a client calls the former `POST /v1/lan/games/{gameId}/start`, `POST /v1/lan/games/{gameId}/reset`, `POST /v1/lan/games/{gameId}/complete`, or `POST /v1/lan/games/{gameId}/cancel` routes
- **THEN** the API MUST NOT expose those routes or aliases

### Requirement: Guid-constrained match and tournament id routes
Match routes under `/v1/lan/matches/{id}` and tournament routes under `/v1/lan/tournaments/{tournamentId}` MUST constrain their id segment to a guid. A malformed id MUST NOT match the route and MUST return 404.

#### Scenario: Malformed match id
- **WHEN** a client requests `GET /v1/lan/matches/not-a-guid`
- **THEN** the API MUST return 404

#### Scenario: Malformed tournament id
- **WHEN** a client requests `GET /v1/lan/tournaments/not-a-guid`
- **THEN** the API MUST return 404
