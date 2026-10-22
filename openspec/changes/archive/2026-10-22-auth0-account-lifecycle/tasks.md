# Tasks

- [x] 1. Add persistent subject fences and encrypted pending-cleanup state with additive schema migration and resumable secure backfill.
- [x] 2. Add exact-subject Auth0 deletion, durable dispatch/retry, operator status/retry endpoints, and 202 responses to all deletion routes.
- [x] 3. Enforce subject freshness and pin local account incarnation across authenticated APIs and hub connections/invocations; coordinate registration under the subject lock.
- [x] 4. Cover legacy backfill, failure/retry, reused-subject replay, request races, and all deletion routes with focused tests.
- [x] 5. Document shared encryption-key configuration, migration and deployment order, operator handling, and external Auth0 GET/DELETE race.
