namespace IntoChat.OS.Modules.Apps;

// Where an installed copy came from: the publisher's name, the source its bundle is read from,
// its name for the app, and the published version the copy was taken at. Consented is what the
// person accepted at install or update; a copy recorded before consent was asked holds null, and
// its installed manifest's requirements stand for it. PackageDigest is the host-shipped compiled package the copy runs:
// set, the program is that prebuilt package (until the person edits the copy); null, it is compiled from the copy's sources.
public sealed record AppOrigin(
    string Publisher,
    AppSource Source,
    string Name,
    string Version,
    string? PublicationDigest = null,
    string? InstalledByAccount = null,
    AppRequirements? Consented = null,
    string? PackageDigest = null);
