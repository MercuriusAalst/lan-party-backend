## Purpose

Define self-service identity pictures and their safe use in existing user and player projections.

## Requirements

### Requirement: Manage own profile picture
The system SHALL let an authenticated user upload, replace, or remove their own profile picture through `/v1/lan/users/me/picture`, using the existing media validation and storage behavior. The system SHALL derive ownership from the authenticated identity and SHALL return the existing current-user profile response with nullable `user.profilePictureUrl`.

#### Scenario: Upload or replace a picture
- **WHEN** an authenticated active user sends a valid image in multipart field `picture`
- **THEN** the system stores its media reference on that user and returns the profile response with the picture URL

#### Scenario: Remove a picture
- **WHEN** an authenticated active user deletes their picture
- **THEN** the stored reference is cleared and the profile response reports a null picture URL

#### Scenario: Reject invalid or unauthenticated request
- **WHEN** a request is unauthenticated or its image is invalid
- **THEN** the system returns the existing unauthorized or validation error and does not change the current picture reference

### Requirement: Keep picture references safe through account lifecycle
The system SHALL persist the nullable user picture reference and coordinate picture mutation with account anonymization. It SHALL retain the previous reference when saving a replacement fails, clean up unreferenced newly stored media after a failed save, and perform old-image cleanup best-effort after a successful update.

#### Scenario: Failed replacement preserves current picture
- **WHEN** storing a new picture reference fails after media upload
- **THEN** the previous reference remains and the newly uploaded image is removed only when it is definitely unreferenced

#### Scenario: Anonymization cleans up safely
- **WHEN** the user account is anonymized
- **THEN** the picture reference is cleared under the same mutation lock and old media cleanup is best-effort

### Requirement: Expose pictures in existing identity projections
The system SHALL include nullable picture URLs in existing user and player identity projections that already expose that identity, using bounded batched reads for collections and preserving existing privacy rules and snapshot identity fallback behavior.

#### Scenario: Existing identity projections include picture URLs
- **WHEN** an existing identity or player projection returns a user
- **THEN** it includes that user’s nullable picture URL without adding per-user queries or changing visibility

