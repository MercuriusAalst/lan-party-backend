# harden-api-security-and-operations

- [x] Throw `ForbiddenException` with `team_captain_required`, `match_participant_required` and `match_own_side_required` codes for captain and participant checks.
- [x] Return mapped exceptions as ProblemDetails with `message` and `code` extensions and remove the unused `ExceptionFilter`.
- [x] Set a fallback authorization policy requiring an authenticated user and test that every endpoint declares `Authorize` or `AllowAnonymous`.
- [x] Map anonymous, non-rate-limited `/health/live` and `/health/ready` (database check) probes.
- [x] Serve `/images`, `/staticfiles` and the Swagger UI ahead of the security pipeline.
- [x] Store uploads as lossy WebP with Imageflow size limits on upload and on `/images` processing; map Imageflow failures to 400.
- [x] Fail startup outside Development without `FileStorage:Location` or `Auth0:Audience`; read CORS origins from `Cors:AllowedOrigins`; gate startup migrations on `Database:ApplyMigrationsOnStartup`.
- [x] Add opt-in OpenTelemetry, JSON console logging outside Development and the `mercurius.outbox.dead_lettered` counter.
- [x] Cover the changes with host composition, exception handler, media and eventing tests.
