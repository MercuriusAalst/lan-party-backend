## Persistence and API

- [x] Add a Tournament-owned persisted ordered featured-tournament selection and EF configuration.
- [x] Compose the persistence through Tournament module boundaries and add a reversible PostgreSQL migration/model snapshot update.
- [x] Add an anonymous GET returning ordered IDs and the minimal homepage card projection; use the current public order as the unsaved fallback.
- [x] Add an admin-authorized atomic PUT that validates exactly four distinct existing non-canceled tournaments before replacing the selection.
- [x] Preserve valid saved order and deterministically backfill ineligible or deleted selections without duplicates; return fewer than four when fewer are eligible.
- [x] Keep reads uncached so committed replacements are immediately visible across replicas.

## Verification

- [x] Cover route authorization, validation, ordered persistence, fallback, ineligible selection recovery, and minimal response shape.
- [x] Strictly validate OpenSpec before implementation and after synchronizing completed tasks.
- [x] Run focused API tests and the backend solution build.
