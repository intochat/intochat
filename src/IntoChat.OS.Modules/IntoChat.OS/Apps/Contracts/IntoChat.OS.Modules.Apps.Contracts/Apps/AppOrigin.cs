namespace IntoChat.OS.Modules.Apps;

// Where an installed copy came from: the publisher's name, the source its bundle is read from,
// its name for the app, and the published version the copy was taken at. Consented is what the
// person accepted at install or update; a copy recorded before consent was asked holds null, and
// its installed manifest's requirements stand for it. PackageDigest is the host-shipped compiled package the copy runs:
// set, the program is that prebuilt package (until the person edits the copy); null, it is compiled from the copy's sources.
[GenerateSerializer, Alias("apps.origin")]
public sealed record AppOrigin(
    [property: Id(0)] string Publisher,
    [property: Id(1)] AppSource Source,
    [property: Id(2)] string Name,
    [property: Id(3)] string Version,
    [property: Id(4)] string? PublicationDigest = null,
    [property: Id(5)] string? InstalledByAccount = null,
    [property: Id(6)] AppRequirements? Consented = null,
    [property: Id(7)] string? PackageDigest = null);
