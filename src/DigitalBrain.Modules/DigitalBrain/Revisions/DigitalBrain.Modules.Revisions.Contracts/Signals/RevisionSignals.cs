namespace DigitalBrain.Revisions.Signals;

[GenerateSerializer, Alias("revisions.changed")] public sealed record RevisionChanged([property: Id(0)] string Sha, [property: Id(1)] RevisionStage Stage) : Signal;
[GenerateSerializer, Alias("revisions.building")] public sealed record RevisionBuilding([property: Id(0)] string Sha) : Signal;
[GenerateSerializer, Alias("revisions.started")] public sealed record RevisionStarted([property: Id(0)] string Sha, [property: Id(1)] string McpEndpoint) : Signal;
[GenerateSerializer, Alias("revisions.healthy")] public sealed record RevisionHealthy([property: Id(0)] string Sha) : Signal;
[GenerateSerializer, Alias("revisions.verified")] public sealed record RevisionVerified([property: Id(0)] string Sha, [property: Id(1)] VerificationReport Report) : Signal;
[GenerateSerializer, Alias("revisions.promoted")] public sealed record RevisionPromoted([property: Id(0)] string Sha) : Signal;
[GenerateSerializer, Alias("revisions.failed")] public sealed record RevisionFailed([property: Id(0)] string Sha, [property: Id(1)] RevisionStage Stage, [property: Id(2)] string Reason) : Signal;