namespace IntoChat.OS.Modules.Apps;

// The Builder's files: the unit's .cs plus spec.md and scenarios.json.
internal sealed record BuildDraft(Dictionary<string, string> Files, AppManifest? Manifest);

// The Creator's two unit calls as the build flow uses them: the Author describes the unit, the Builder writes it. Each reports no
// progress; the flow's step signals are the progress.
internal interface IDrafter
{
    Task<AuthoredUnit> Author(string brainId, string request, IReadOnlyDictionary<string, string>? existing, CancellationToken ct);

    Task<BuildDraft> Draft(string brainId, AuthoredUnit unit, IReadOnlyDictionary<string, string>? existing, CancellationToken ct);
}
