## Why

The homepage can keep its primary featured tournament while showing fewer than four cards when that better fits the available content. The current admin contract and database constraint require exactly four selections, so administrators cannot save a smaller curated set.

## What Changes

- Allow administrators to replace the ordered selection with one to four distinct eligible tournament IDs.
- Keep unsaved public reads at up to four eligible tournaments, while saved reads preserve the configured count and order and backfill ineligible entries only up to that count.
- Replace the database's exact-four check with a one-to-four range check using a forward migration.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `featured-homepage-tournaments`: Change the saved selection range and make saved-read fallback honor its configured count.

## Impact

- The existing admin PUT and public GET keep their routes and DTO shapes.
- Tournament service validation, public fallback, EF configuration, migration history, and focused tests change.
- The frontend can submit and render selections from one to four items; no backend count field or new dependency is required.
