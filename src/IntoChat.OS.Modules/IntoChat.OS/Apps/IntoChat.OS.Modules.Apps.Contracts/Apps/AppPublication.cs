namespace IntoChat.OS.Apps;

[GenerateSerializer, Alias("apps.publication-ref")]
public sealed record AppPublicationRef(
    [property: Id(0)] string Publisher,
    [property: Id(1)] AppSource.Brain Source,
    [property: Id(2)] string Name,
    [property: Id(3)] string Version,
    [property: Id(4)] string Digest);

[GenerateSerializer, Alias("apps.publication")]
public sealed record AppPublication(
    [property: Id(0)] AppPublicationRef Reference,
    [property: Id(1)] DateTimeOffset PublishedAt,
    [property: Id(2)] AppManifest Manifest,
    [property: Id(3)] string? Preview);
