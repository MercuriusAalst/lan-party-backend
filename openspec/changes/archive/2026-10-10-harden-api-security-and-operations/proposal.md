# Harden API security and operations

## Why

Authorization failures were reported inconsistently (captain and participant checks answered 401 as if the caller were anonymous), error bodies had no stable shape, and endpoints were anonymous unless they opted in to authorization. Production deployments also lacked health probes, fail-fast configuration, configurable CORS and telemetry, and image uploads were stored as unbounded lossless WebP.

## What Changes

- Captain and match-participant checks answer 403 with a stable `code`; a missing authenticated user id stays 401.
- Mapped API errors are returned as `application/problem+json` with `message` and `code` extensions.
- A fallback authorization policy requires an authenticated user for every endpoint that is not explicitly anonymous.
- Anonymous `/health/live` and `/health/ready` probes; `/images`, `/staticfiles`, Swagger and the probes are not rate limited.
- Uploads are stored as lossy WebP (quality 82) within 8000x8000 / 40 MP decode, frame and encode limits; oversized or undecodable images return 400.
- Startup fails outside Development without `FileStorage:Location` or `Auth0:Audience`; CORS origins come from `Cors:AllowedOrigins`; startup migrations follow `Database:ApplyMigrationsOnStartup`.
- Opt-in OpenTelemetry export, JSON console logs outside Development, and an outbox dead-letter counter.
