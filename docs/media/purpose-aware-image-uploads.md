# Purpose-aware image uploads

## Runtime policy

Every Media upload now requires an explicit `MediaPurpose`. The centralized policy in
`src/Modules/Media/Mercurius.Modules.Media/Infrastructure/ImagePolicy.cs` maps it to a
scale-down-only bounding box and one WebP encoder:

| Purpose | Maximum bounds | Encoder |
| --- | ---: | --- |
| Tournament banner | 2560x1440 | lossy WebP, quality 78 |
| Sponsor logo | 1024x1024 | lossless WebP |
| Team logo | 1024x1024 | lossless WebP |
| Profile picture | 512x512 | lossy WebP, quality 82 |

The existing Imageflow pipeline decodes once, applies `ConstrainWithin`, and encodes once.
It does not crop, stretch or upscale. The 8000x8000 and 40-megapixel decode/frame/encode
bounds, supported input types, configured upload cap, generated URLs, cancellation cleanup
and consumer lifecycle behavior remain in place. GIF input follows the same conversion path
as before.

## Reproducible benchmark

The benchmark helper is in the existing Media test project. From the repository root:

```powershell
$env:MEDIA_BENCHMARK_OUTPUT = Join-Path (Get-Location) 'docs/media/benchmark'
dotnet test tests/Mercurius.Modules.Media.Tests/Mercurius.Modules.Media.Tests.csproj --filter "FullyQualifiedName~ImagePolicyBenchmarkTests"
```

It generates deterministic PNG inputs, old full-size quality-82 WebP outputs, new Media-policy
outputs, logo lossy-vs-lossless outputs, and `measurements.csv`. Each production result is saved
once through `IMediaModule`; the comparison variants are separate offline benchmark outputs.

Measured results:

| Fixture | Input | Old quality 82 | New policy | Reduction |
| --- | ---: | ---: | ---: | ---: |
| Banner artwork with large and fine block text | 5000x2813 | 37,208 B at 5000x2813 | 14,294 B at 2560x1440 | 61.6% |
| Detailed LAN party photograph | 3840x2560 | 3,112,906 B at 3840x2560 | 512,632 B at 2160x1440 | 83.5% |
| Already-small banner | 320x180 | 1,154 B at 320x180 | 1,052 B at 320x180 | 8.8% |

| Logo fixture | Lossy quality 86 | Lossless | Lossless relative size |
| --- | ---: | ---: | ---: |
| Transparent logo with block text | 3,602 B | 858 B | 76.2% smaller |
| Flat-color transparent artwork | 4,020 B | 896 B | 77.7% smaller |

Visual inspection covered the text/artwork banner, the real photo baseline and policy previews,
the small no-upscale case, logo text and flat alpha artwork under `docs/media/benchmark/`.
The large and fine text remain clear, and the real photo retains composition and detail without
becoming visibly blurred at normal display size. Lossless is selected for logos because it was
both smaller and visually equivalent on these fixtures. The automated alpha test also verifies
that output transparency survives the round trip.

The detailed photo is
[Lyon E-Sport 9 - Palais des sports de Lyon - LAN party](https://commons.wikimedia.org/wiki/File:Lyon_E-Sport_9_-_Palais_des_sports_de_Lyon_-_LAN_party.jpg)
by Xavier Care, licensed under CC BY-SA 4.0. Its 3840x2560, 3,540,004-byte source derivative is
inside the existing 5 MiB and 40-megapixel upload bounds. Attribution and derivative terms are in
`docs/media/benchmark/source/ATTRIBUTION.md`. The remaining inputs are deterministic synthetic
graphics for repeatability. These results describe this fixture set and are not a universal byte
or quality guarantee for every uploaded photograph.

## Historical encoding evidence

Repository history shows that the upload module used `WebPLosslessEncoder` before commit
`164e4ec891230a11cae3b52940e6fd16eae7e59a` (`fix(media): bound Imageflow frame sizes and store
uploads as lossy WebP`). That commit, dated 2026-10-13, changed production uploads to lossy WebP
quality 82. Therefore existing assets can be a mixture of legacy lossless WebP and later
quality-82 WebP. Current dimensions and file sizes must be inventoried; storage timestamps alone
are not sufficient proof of the original encoder.

No existing assets are re-encoded by this change.

## Existing-asset follow-up

Treat this as a separate, report-first maintenance operation. It is not part of an upload
request and must not run automatically.

1. Take a read-only inventory of local generated `images/<key>.webp` assets and every persisted
   reference. The source-of-truth references are
   `tournament.tournaments.ImageUrl`, `sponsorship.sponsors.LogoUrl`,
   `teams.teams.LogoUrl`, and `identity.users.ProfilePictureUrl`.
   `tournament.tournament_registrations.TeamLogoUrlAtRegistration` is a historical snapshot and
   remains a retirement guard. `discovery.search_documents.image_url` is a projection and must
   also be counted until refreshed.
2. Report each asset's dimensions, byte size, owning current entity, historical snapshot count,
   search-document count and candidate purpose. Do not mark unreferenced files for deletion until
   the report is reviewed.
3. For each approved current entity, create a new asset through Media with the correct purpose.
   Store its new generated URL and never overwrite the existing file under its old URL. The old
   URL may be cached by clients for up to one year.
4. Update one owning reference at a time through the established service and transaction paths so
   authorization, concurrency and event behavior remain intact. Refresh affected discovery
   documents after source updates. Do not rewrite a historical registration snapshot merely to
   make cleanup easier.
5. Verify HTTP serving, dimensions, byte size, visual quality and the new reference before
   proceeding. On failure, restore the prior reference and record the new unreferenced asset for
   later retirement.
6. Retire an old file only after a fresh whole-database reference query finds no current,
   historical or search-document reference. Use the existing Media deletion path. If that query
   or deletion is uncertain, keep the file and record it for manual remediation.

Because deletion cannot remove already cached responses, an old cached URL may remain available
to clients. It is no longer referenced after the database update, so it does not break historical
references.

## Frontend follow-up

Responsive `srcset`/`sizes` delivery and optional Imageflow URL resizing remain separate frontend
work. Upload compression does not replace right-sized delivery, and neither change addresses the
separately observed GPU cost of hero blur and transform effects.
