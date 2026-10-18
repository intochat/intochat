namespace IntoChat.OS.Microsoft.Aspire.Candidates;

[GenerateSerializer, Alias("aspire.candidate.state")]
[ExportableState]
internal sealed record CandidateState
{
    [Id(0)] public string Branch { get; init; } = "";
    [Id(1)] public string Summary { get; init; } = "";
    [Id(2)] public CandidateStage Stage { get; init; } = CandidateStage.Proposed;
    [Id(3)] public CandidateEndpoints? Endpoints { get; init; }
    [Id(4)] public VerificationPlan? Plan { get; init; }
    [Id(5)] public VerificationReport? Report { get; init; }
    [Id(6)] public string? Failure { get; init; }
    [Id(7)] public CandidateStage[] History { get; init; } = [];
}
