## ADDED Requirements

### Requirement: Endpoints require authentication by default
The API host MUST apply a fallback authorization policy that requires an authenticated user for every endpoint not explicitly marked anonymous. Every mapped endpoint MUST declare either an authorization requirement or anonymous access. The Swagger document and UI MUST remain anonymous.

#### Scenario: Anonymous caller requests an unmapped path
- **WHEN** an anonymous caller requests a path that no endpoint maps
- **THEN** the API MUST return HTTP 401

#### Scenario: Anonymous caller requests Swagger
- **WHEN** an anonymous caller requests the Swagger UI or document
- **THEN** the API MUST serve it

### Requirement: Health probes
The API host MUST expose anonymous `/health/live` and `/health/ready` endpoints that are not rate limited. `/health/live` MUST report healthy without running dependency checks; `/health/ready` MUST check database connectivity.

#### Scenario: Liveness probe
- **WHEN** an anonymous caller requests `/health/live`
- **THEN** the API MUST return HTTP 200 without querying the database

#### Scenario: Readiness probe with unreachable database
- **WHEN** `/health/ready` is requested and the database cannot be reached
- **THEN** the API MUST report unhealthy

### Requirement: Production configuration is explicit
Outside the Development environment the API host MUST fail at startup when `FileStorage:Location` or `Auth0:Audience` is missing or blank. CORS allowed origins MUST come from `Cors:AllowedOrigins`, defaulting to `https://*.mercurius-aalst.be` with wildcard subdomains. Startup migrations MUST run only when `Database:ApplyMigrationsOnStartup` is true, which is the default.

#### Scenario: Required setting is missing in production
- **WHEN** the host starts outside Development without `FileStorage:Location` or `Auth0:Audience`
- **THEN** startup MUST fail with an error naming the missing key

#### Scenario: Migrations are disabled
- **WHEN** `Database:ApplyMigrationsOnStartup` is false
- **THEN** the host MUST start without applying migrations

### Requirement: Opt-in observability
The API host MUST write JSON console logs outside Development. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set it MUST export traces, metrics and logs over OTLP, including ASP.NET Core, HttpClient, runtime, Npgsql and `Mercurius.Eventing` telemetry; when it is unset no OpenTelemetry exporter MUST be registered. The outbox dispatcher MUST increment the `mercurius.outbox.dead_lettered` counter, tagged with `event_type`, each time it dead-letters a message.

#### Scenario: OTLP endpoint is not configured
- **WHEN** the host starts without `OTEL_EXPORTER_OTLP_ENDPOINT`
- **THEN** no telemetry MUST be exported

#### Scenario: Outbox message is dead-lettered
- **WHEN** the dispatcher dead-letters an outbox message after exhausting its attempts
- **THEN** `mercurius.outbox.dead_lettered` MUST increase by one with the message's event type
