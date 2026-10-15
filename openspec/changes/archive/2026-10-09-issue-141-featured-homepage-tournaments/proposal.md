# Persist and expose featured homepage tournaments

Issue #141 allows administrators to curate the four homepage tournament cards in a stable order.

The Tournament module will persist one ordered selection, expose a lightweight public projection for the homepage, and provide an admin-only replace-all operation. Public reads will retain the existing automatic tournament order until a selection is saved, then preserve still-eligible selected tournaments and fill vacancies from the same deterministic public order. Tournament status is the only available publication signal, so canceled tournaments are ineligible.

This change does not add per-tournament featured flags, a CMS, or server-side response caching. The database is authoritative across API replicas; reads query current state so a successful replacement is visible immediately.

## Scope

- Add Tournament-owned persistence for the ordered four-ID selection and its PostgreSQL migration.
- Add anonymous GET and admin PUT contracts at `/v1/lan/featured-tournaments`.
- Validate a complete set of four distinct existing non-canceled tournaments before replacing the saved value.
- Return ordered IDs and only the fields required by homepage tournament cards.
- Preserve valid saved order and deterministically fill invalidated or missing selections in public reads.
- Add focused route, permission, persistence, validation, fallback, and projection coverage.

## Out of scope

- A per-tournament featured property or generic homepage content management.
- Any change to tournament lifecycle, bracket, or registration behavior.
- Response caching or a cache invalidation service; this API currently has no server-side response cache.
