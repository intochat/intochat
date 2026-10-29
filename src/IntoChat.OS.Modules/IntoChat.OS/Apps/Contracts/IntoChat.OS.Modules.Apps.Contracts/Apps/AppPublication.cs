namespace IntoChat.OS.Modules.Apps;

public sealed record AppPublicationRef(
    string Publisher,
    AppSource.Brain Source,
    string Name,
    string Version,
    string Digest);

public sealed record AppPublication(
    AppPublicationRef Reference,
    DateTimeOffset PublishedAt,
    AppManifest Manifest,
    string? Preview);
