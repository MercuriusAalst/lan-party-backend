namespace Mercurius.Modules.Media.Contracts;

public sealed record MediaUpload(
    Stream Content,
    string FileName,
    string ContentType,
    long Length,
    MediaPurpose Purpose);
