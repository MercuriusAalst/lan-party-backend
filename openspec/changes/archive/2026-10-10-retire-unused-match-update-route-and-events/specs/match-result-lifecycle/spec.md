## ADDED Requirements

### Requirement: Single administrative resolution route

The API MUST expose administrative score resolution only through `POST /v1/lan/matches/{id}/resolve`. It MUST NOT expose the former `PUT /v1/lan/matches/{id}` route as an alias. `GET /v1/lan/matches/{id}` MUST remain available.

#### Scenario: Admin resolves through the resolve route

- **WHEN** an authenticated admin submits a valid final score to `POST /v1/lan/matches/{id}/resolve`
- **THEN** the API MUST resolve the match exactly as specified by the administrative resolution requirement

#### Scenario: Former update route is unavailable

- **WHEN** a client sends `PUT /v1/lan/matches/{id}`
- **THEN** the API MUST NOT route the request to a match mutation
