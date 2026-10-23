# Tasks

- [x] 1. Store pending cleanup subjects in one private cleanup table and derive pending/cutoff state from permanent cleanup history.
- [x] 2. Add exact-subject Auth0 deletion, durable dispatch/retry, and 202 responses to all deletion routes.
- [x] 3. Enforce subject freshness and pin local account incarnation across authenticated APIs and hub connections/invocations; coordinate registration under the subject lock.
- [x] 4. Cover failure/retry, reused-subject replay, request races, and all deletion routes with focused tests.
- [x] 5. Create the initial cleanup schema without legacy-data repair or upgrade machinery.
- [x] 6. Remove hosted backfill and operator HTTP routes, retain only deletion-pending checks in the lifecycle service, and document database status/safe-retry SQL.
- [x] 7. Document private database trust, permanent history, fresh test schema scope, and external Auth0 GET/DELETE race.
