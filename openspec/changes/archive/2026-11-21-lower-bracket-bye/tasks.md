## Backend implementation

- [x] Specify BYE propagation and guarded administrator recovery.
- [x] Propagate automatic BYE winners through persisted tournament graphs.
- [x] Add transactional, idempotent admin recovery for one tournament, with safe conflict handling.
- [x] Add domain, recovery, conflict, idempotency, runtime graph and admin-route tests.
- [x] Prevalidate downstream BYE records before propagation; test uncompleted conflicts and concurrent reversal in both commit orderings.
- [x] Run the focused domain, bracket, recovery and endpoint-route checks; validate OpenSpec strictly.
- [x] Run PostgreSQL-backed recovery tests, including the already-recorded BYE winner, conflict rollback and repeated recovery.
- [x] Synchronize the live spec and archive this change.
