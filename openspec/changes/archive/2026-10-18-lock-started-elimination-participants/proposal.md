# Lock started elimination tournament participants

Admins can currently remove an individual registration or team after a single or double elimination tournament starts. The bracket keeps the participant while the registration disappears, leaving the tournament state inconsistent.

Freeze admin registration removal for single and double elimination tournaments once they leave Scheduled. Preserve registration removal behavior for other bracket types.

## Scope

- Reject individual and team registration removal from single and double elimination tournaments unless they are Scheduled.
- Coordinate scheduled removals with tournament start through the persisted tournament revision so concurrent changes cannot save a stale bracket.
- Preserve existing registration cleanup, routes, and response shapes.
