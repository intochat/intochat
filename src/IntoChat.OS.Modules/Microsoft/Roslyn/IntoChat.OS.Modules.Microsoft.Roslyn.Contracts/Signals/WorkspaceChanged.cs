using IntoChat.OS;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[GenerateSerializer, Alias("coding.workspace-changed")]
public sealed record WorkspaceChanged(
    [property: Id(0)] string Key,
    [property: Id(1)] long Generation,
    [property: Id(2)] WorkspacePhase Phase) : Signal;
