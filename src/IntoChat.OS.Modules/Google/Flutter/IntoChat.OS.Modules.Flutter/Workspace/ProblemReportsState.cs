namespace IntoChat.OS.Flutter.Workspace;

[GenerateSerializer, Alias("ui.problem-reports-state")]
[ExportableState]
public sealed record ProblemReportsState
{
    [Id(0)] public List<ProblemReport> Reports { get; init; } = [];
}
