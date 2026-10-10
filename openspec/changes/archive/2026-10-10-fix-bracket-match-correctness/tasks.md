# fix-bracket-match-correctness

- [x] Seed elimination brackets so byes go to the top seeds and never pair two empty slots.
- [x] Derive double elimination lower-bracket byes from the linked feeding matches.
- [x] Record bye advancement without a `Guid.Empty` source match.
- [x] Stop match GET endpoints from persisting expired deadlines; apply them in memory only.
- [x] Constrain match and tournament id routes to guids.
- [x] Bound, order, isolate and single-instance the match deadline processor.
- [x] Document the single double elimination grand final.
- [x] Add regression tests for seeding, lower-bracket byes, read-only deadline reads and processor batching.
- [x] Sync delta specs to `openspec/specs/`.
