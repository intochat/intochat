namespace IntoChat.OS.Kernel.Install;

public sealed record ReleaseManifest(
    string Version,
    string Commit,
    string OsImage);
