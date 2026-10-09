namespace DigitalBrain.Revisions;

[GenerateSerializer, Alias("revisions.stage")]
public enum RevisionStage { Proposed, Queued, Starting, Started, Healthy, Verifying, Verified, Promoted, Failed, Discarded }

[GenerateSerializer, Alias("revisions.proposal")]
public sealed record RevisionProposal(
    [property: Id(0)] string Branch,
    [property: Id(1)] string Summary,
    [property: Id(2)] Dictionary<string, string> Files,
    [property: Id(3)] VerificationPlan Plan);

[GenerateSerializer, Alias("revisions.snapshot")]
public sealed record RevisionSnapshot(
    [property: Id(0)] string Sha,
    [property: Id(1)] string Branch,
    [property: Id(2)] string Worktree,
    [property: Id(3)] string Summary,
    [property: Id(4)] RevisionStage Stage,
    [property: Id(5)] string? McpEndpoint,
    [property: Id(6)] VerificationPlan? Plan,
    [property: Id(7)] VerificationReport? Report,
    [property: Id(8)] string? Failure,
    [property: Id(9)] RevisionStage[] History);