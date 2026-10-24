namespace IntoChat.OS.Apps;

// Where an installed copy came from: the publisher's name, the source its bundle is read from,
// its name for the app, and the published version the copy was taken at. Consented is what the
// person accepted at install or update; a copy recorded before consent was asked holds null, and
// its installed manifest's requirements stand for it.
[GenerateSerializer, Alias("apps.origin")]
public sealed record AppOrigin(
    [property: Id(0)] string Publisher,
    [property: Id(1)] AppSource Source,
    [property: Id(2)] string Name,
    [property: Id(3)] string Version,
    [property: Id(4)] string? PublicationDigest = null,
    [property: Id(5)] string? InstalledByAccount = null,
    [property: Id(6)] AppRequirements? Consented = null);
