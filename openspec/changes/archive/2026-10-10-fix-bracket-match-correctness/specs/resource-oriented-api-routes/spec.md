## ADDED Requirements

### Requirement: Guid-constrained match and tournament id routes
Match routes under `/v1/lan/matches/{id}` and tournament routes under `/v1/lan/tournaments/{tournamentId}` MUST constrain their id segment to a guid. A malformed id MUST NOT match the route and MUST return 404.

#### Scenario: Malformed match id
- **WHEN** a client requests `GET /v1/lan/matches/not-a-guid`
- **THEN** the API MUST return 404

#### Scenario: Malformed tournament id
- **WHEN** a client requests `GET /v1/lan/tournaments/not-a-guid`
- **THEN** the API MUST return 404
