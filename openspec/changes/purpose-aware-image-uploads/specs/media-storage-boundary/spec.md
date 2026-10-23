## MODIFIED Requirements

### Requirement: Media stores purpose-optimized WebP images
Media MUST require every production image upload to name its purpose explicitly. It MUST apply a
centralized maximum bounding box for that purpose before encoding, MUST preserve the source aspect
ratio, and MUST NOT crop, stretch or upscale. Media MUST write one WebP output per upload and
MUST continue to enforce the existing 8000x8000 pixel, 40 megapixel and configured 5 MiB input
limits. Invalid, oversized or undecodable images MUST return the existing validation behavior and
MUST NOT leave a stored file.

Tournament banners MUST be bounded to 2560 by 1440 and encoded as lossy WebP at quality 78.
Profile pictures MUST be bounded to 512 by 512 and encoded as lossy WebP at quality 82. Sponsor
and Team logos MUST be bounded to 1024 by 1024 and MUST use one centralized quality-or-lossless
policy selected from representative graphic, text and alpha-image evidence.

#### Scenario: Large tournament banner is uploaded
- **WHEN** an allowed image larger than 2560 by 1440 is uploaded as a tournament banner
- **THEN** the stored image fits within 2560 by 1440 with its aspect ratio preserved
- **AND** it is encoded once as quality-78 lossy WebP

#### Scenario: Portrait or landscape image is bounded
- **WHEN** an allowed portrait or landscape image exceeds either dimension of its purpose's bounding box
- **THEN** both output dimensions fit within the bounding box and the shorter dimension is reduced in proportion

#### Scenario: Small image is uploaded
- **WHEN** an allowed image already fits within its purpose's bounding box
- **THEN** Media does not upscale or crop it

#### Scenario: Logo and profile image policies are distinct
- **WHEN** a Sponsor or Team logo and a profile picture are uploaded
- **THEN** each uses its explicit 1024-by-1024 or 512-by-512 maximum dimension policy
- **AND** the caller cannot select an undeclared purpose or omit the purpose

#### Scenario: Existing upload safeguards are exercised
- **WHEN** an upload uses an unsupported type, exceeds the configured byte limit, exceeds the decode bounds or cannot be decoded
- **THEN** Media retains its existing validation status and cleanup behavior

#### Scenario: Transparency, orientation and GIF input are preserved as compatible
- **WHEN** a transparent or oriented image, or an allowed GIF, is uploaded
- **THEN** the stored WebP retains the applicable alpha or orientation and follows the existing GIF conversion behavior
