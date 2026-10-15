## Context

The saved selection is an ordered PostgreSQL UUID array. The service currently requires exactly four distinct eligible IDs, public reads always fill saved selections to four, and a database check enforces array cardinality four. The homepage still needs the same lightweight card projection and the first selected ID remains the primary card.

## Goals / Non-Goals

**Goals:**
- Accept saved selections containing one through four distinct eligible tournament IDs.
- Preserve saved order and use its length as the target for fallback cards.
- Keep the unsaved default at up to four cards.
- Keep persistence validation consistent with service validation.

**Non-Goals:**
- Changing routes or response DTOs.
- Adding a separate count field or changing sponsor projection behavior.
- Changing which tournament statuses are eligible.

## Decisions

- Keep the existing UUID array as the sole source of order and configured count. On saved reads, fill only until the saved array length is reached; return fewer when fewer eligible tournaments exist. Deduplicate malformed persisted values as today.
- Leave the no-selection query capped at four and deterministically ordered.
- Replace the exact-four check constraint with `cardinality("TournamentIds") BETWEEN 1 AND 4` in configuration and a new forward migration. Existing four-item rows satisfy this constraint without data changes.
- In migration `Down`, restore the exact-four constraint without rewriting stored arrays. PostgreSQL rejects the constraint change transactionally if a partial selection exists, leaving the data and widened constraint intact.

## Risks / Trade-offs

- [Downgrade can be blocked after partial selections are saved] → PostgreSQL rejects the narrower constraint and rolls back the migration; an operator can restore a four-item selection before retrying.
- [Application validation alone cannot protect direct database writes] → The new database check independently rejects empty and over-four arrays.

## Migration Plan

Apply the new migration after the existing featured-tournaments migration. It replaces only the cardinality constraint; the singleton constraint and all stored arrays remain unchanged. Rollback preserves data and succeeds only while every saved array has four entries.

## Open Questions

None.
