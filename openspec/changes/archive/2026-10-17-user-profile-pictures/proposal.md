# User profile pictures

## Why
Users need a consistent way to identify themselves in search, teams, and tournament results, and to manage that image from their own account.

## What Changes
- Add self-service upload, replacement, and removal of a profile picture using existing media validation and storage.
- Store a nullable picture reference on users and propagate it through existing identity and player projections.
- Coordinate picture mutation and account anonymization with the existing database lock and safe cleanup behavior.

## Decisions
- `PUT /v1/lan/users/me/picture` accepts multipart field `picture`; `DELETE` uses the same path. Both return the existing current-user profile response, whose nested user gains nullable `profilePictureUrl`.
- The current user comes only from authenticated claims. Existing profile visibility rules remain unchanged.
- Images use the existing media module and its current supported formats, size limit and generated filenames.
- User picture mutation and account anonymization serialize through the database advisory-lock pattern already used for media mutations. Persist the new reference before cleaning an old image; preserve references on failed updates and only compensate a new image when it is definitely unreferenced.
