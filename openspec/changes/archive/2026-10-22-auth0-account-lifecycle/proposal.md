# Complete Auth0 Account Deletion And Fence Reused Identities

Deleting a LAN account currently anonymizes only the local profile. The Auth0 identity remains usable, and a recreated social identity can reuse the exact same subject, allowing a still-valid old token to resolve to a later LAN account.

## Scope

- Detach every deleted LAN row from its provider subject and persist exact-subject cleanup work atomically with anonymization.
- Retry and expose provider cleanup failures without claiming deletion is complete before Auth0 confirms it.
- Establish a durable token freshness boundary so a prior token cannot access a re-registered LAN account with the same subject.
- Preserve local identity history and existing deletion events while returning a pending cleanup response from every deletion route.

## Non-goals

- Frontend implementation, generic outbox redesign, and atomicity across the LAN database and Auth0.