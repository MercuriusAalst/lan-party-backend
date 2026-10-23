# Complete Auth0 Account Deletion And Fence Reused Identities

Deleting a LAN account currently anonymizes only the local profile. The Auth0 identity remains usable, and a recreated social identity can reuse the exact same subject, allowing a still-valid old token to resolve to a later LAN account.

## Scope

- Detach every deleted LAN row from its provider subject and persist exact-subject cleanup work atomically with anonymization in one private cleanup table.
- Retry provider cleanup through the existing outbox without claiming deletion is complete before Auth0 confirms it.
- Derive the pending state and durable token freshness boundary from permanent cleanup history.
- Preserve local identity history and existing deletion events while returning a pending cleanup response from every deletion route.
- Provide database-only status and safe-retry SQL for operations instead of new API routes.

## Non-goals

- Frontend implementation, generic outbox redesign, separate encryption, and atomicity across the LAN database and Auth0.
