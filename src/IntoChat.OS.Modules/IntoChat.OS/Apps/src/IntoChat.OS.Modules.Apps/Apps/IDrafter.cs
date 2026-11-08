namespace IntoChat.OS.Modules.Apps;

// What a build drafts from: a unit is a goal alone; an app is its snapshot (spec, manifest, current files) and the person's instruction.
internal sealed record BuildRequest(string Brain, string Kind, string Goal, string? AppKey, string? Instruction, AppSnapshot? Snapshot);

// The Builder's files, and for an app the manifest they land with.
internal sealed record BuildDraft(Dictionary<string, string> Files, AppManifest? Manifest);

// The Creator's Builder loop as one call; it reports no progress, the flow's step signals are the progress.
internal interface IDrafter
{
    Task<BuildDraft> Draft(BuildRequest request, CancellationToken ct);
}
