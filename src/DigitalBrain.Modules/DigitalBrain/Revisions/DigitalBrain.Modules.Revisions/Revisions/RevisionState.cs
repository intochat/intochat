namespace DigitalBrain.Revisions;

[GenerateSerializer, Alias("revisions.revision-state")]
internal sealed record RevisionState
{
    [Id(0)] public string Branch { get; init; } = "";
    [Id(1)] public string Worktree { get; init; } = "";
    [Id(2)] public string Summary { get; init; } = "";
    [Id(3)] public RevisionStage Stage { get; init; } = RevisionStage.Proposed;
    [Id(4)] public string? McpEndpoint { get; init; }
    [Id(5)] public VerificationPlan? Plan { get; init; }
    [Id(6)] public VerificationReport? Report { get; init; }
    [Id(7)] public string? Failure { get; init; }
    [Id(8)] public RevisionStage[] History { get; init; } = [];
}