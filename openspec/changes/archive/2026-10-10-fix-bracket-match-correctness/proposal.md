# Fix bracket and match correctness

## Why

Elimination brackets could pair two byes in the first round, give a participant two free rounds, or eliminate a lower-bracket participant without playing. Bye advancement stored an empty source match id. Match GET endpoints persisted expired deadlines and published events as a side effect of a read, and the deadline processor saved all expired matches in one unbounded batch on every instance, so one concurrency conflict discarded the whole batch. Malformed match and tournament ids reached model binding and returned 400.

## What Changes

- Seed single and double elimination with classic seeding: the first round has `nextPow2(n) - n` byes, all given to the top seeds, never a BYE-vs-BYE match.
- Mark a double elimination lower-bracket slot as BYE only when its feeding match can never produce a participant.
- Keep a single double elimination grand final without a bracket reset (intentional, now documented).
- Record bye advancement with a null source match.
- Make `GET /v1/lan/matches/{id}` and `GET /v1/lan/matches/{id}/me` read-only; only the background deadline processor persists expired deadlines and publishes their events.
- Process at most 100 expired matches per poll, oldest deadline first, each saved in isolation, on one instance at a time via a PostgreSQL advisory lock.
- Add `:guid` route constraints to match and tournament id routes; malformed ids return 404.

## Known limitations

Double elimination with exactly two participants cannot complete. This predates the change and awaits a product decision.
