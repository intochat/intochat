namespace IntoChat.OS.Modules.Apps;

// The share format: a published version's content. No connections, settings as defaults only.
public sealed record AppBundle(
    string Name,
    string Version,
    AppManifest Manifest,
    string Spec,
    Dictionary<string, string> Files,
    string? CompiledType = null);
