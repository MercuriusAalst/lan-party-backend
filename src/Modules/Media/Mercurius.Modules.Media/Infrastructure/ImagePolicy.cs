using Imageflow.Fluent;
using Mercurius.Modules.Media.Contracts;
using Mercurius.Modules.Shared.Exceptions;

namespace Mercurius.Modules.Media.Infrastructure;

internal readonly record struct ImagePolicy(
    uint MaxWidth,
    uint MaxHeight,
    float LossyQuality,
    bool Lossless)
{
    public static ImagePolicy For(MediaPurpose purpose) => purpose switch
    {
        MediaPurpose.TournamentBanner => new(2560, 1440, 78, false),
        MediaPurpose.SponsorLogo => new(1024, 1024, 0, true),
        MediaPurpose.TeamLogo => new(1024, 1024, 0, true),
        MediaPurpose.ProfilePicture => new(512, 512, 82, false),
        _ => throw new ValidationException("Unsupported image purpose.")
    };

    public IEncoderPreset CreateEncoder() =>
        Lossless ? new WebPLosslessEncoder() : new WebPLossyEncoder(LossyQuality);
}
