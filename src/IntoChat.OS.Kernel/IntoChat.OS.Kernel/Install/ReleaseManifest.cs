namespace IntoChat.OS.Modules.Microsoft.Azure.Install;

public sealed record ReleaseManifest(
    string Version,
    string Commit,
    string OsImage,
    string SandboxImage);
