namespace IntoChat.OS.Modules.Apps;

// The share format: a published version's content. No connections, settings as defaults only.
[GenerateSerializer, Alias("apps.bundle")]
public sealed record AppBundle(
    [property: Id(0)] string Name,
    [property: Id(1)] string Version,
    [property: Id(2)] AppManifest Manifest,
    [property: Id(3)] string Spec,
    [property: Id(4)] Dictionary<string, string> Files);
