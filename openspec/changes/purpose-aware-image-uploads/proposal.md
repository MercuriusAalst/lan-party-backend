## Why

Every uploaded image is currently encoded as quality 82 WebP at its source dimensions. Large
tournament banners therefore remain unnecessarily large, while logos and profile pictures are
stored above the dimensions needed by their consumers. The upload pipeline also has no explicit
business purpose, so each consumer cannot receive an appropriate size and compression policy.

## What Changes

- Require an explicit image purpose in the internal Media upload contract and update all four
  production consumers without changing public routes, DTOs or response shapes.
- Apply an aspect-preserving maximum bounding box before a single WebP encode:
  tournament banners 2560 by 1440, logos 1024 by 1024, and profile pictures 512 by 512.
- Keep lossy quality 78 for tournament banners and quality 82 for profile pictures. Select one
  production logo encoder after measuring lossy and lossless output on representative graphic,
  text and alpha images.
- Preserve small inputs without upscaling, preserve orientation and transparency where applicable,
  and retain current input formats, upload-size limits, decode bounds and GIF behavior.
- Add focused policy, image-quality and consumer-purpose tests plus a reproducible benchmark with
  visual inspection evidence.
- Document the older lossless encoding history and a separate dry-run procedure for safely
  replacing existing large assets with new URLs. Do not rewrite live assets in this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `media-storage-boundary`: Replace the single quality-82 output policy with required
  purpose-specific bounding boxes and compression policies while retaining the existing upload,
  storage, security and lifecycle contract.

## Impact

- Affects Media contracts and encoding, Tournament, Sponsorship, Teams and Identity upload callers,
  their test doubles and media fakes, focused module tests, and Media documentation.
- Adds no package, project, migration, public API field or route.
- Existing stored images are untouched. They require a separate report-first reoptimization
  procedure because their immutable public URLs may be cached.
