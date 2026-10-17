# User profile pictures

Add authenticated self-service profile picture upload, replacement and removal. Persist a nullable image reference on the user and include its URL in existing identity and player read projections where user identity is already returned.

## Decisions

- `PUT /v1/lan/users/me/picture` accepts multipart field `picture`; `DELETE` uses the same path. Both return the existing current-user profile response, whose nested user gains nullable `profilePictureUrl`.
- The current user comes only from authenticated claims. Existing profile visibility rules remain unchanged.
- Images use the existing media module and its current supported formats, size limit and generated filenames.
- User picture mutation and account anonymization serialize through the database advisory-lock pattern already used for media mutations. Persist the new reference before cleaning an old image; preserve references on failed updates and only compensate a new image when it is definitely unreferenced.
