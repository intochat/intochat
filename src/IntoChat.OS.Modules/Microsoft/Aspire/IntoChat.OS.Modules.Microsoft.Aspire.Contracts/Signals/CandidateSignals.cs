using IntoChat.OS;

namespace IntoChat.OS.Microsoft.Aspire;

[GenerateSerializer, Alias("aspire.candidate.changed")] public sealed record CandidateChanged([property: Id(0)] string Sha, [property: Id(1)] CandidateStage Stage) : Signal;
[GenerateSerializer, Alias("aspire.candidate.started")] public sealed record CandidateStarted([property: Id(0)] string Sha, [property: Id(1)] CandidateEndpoints Endpoints) : Signal;
[GenerateSerializer, Alias("aspire.candidate.healthy")] public sealed record CandidateHealthy([property: Id(0)] string Sha) : Signal;
[GenerateSerializer, Alias("aspire.candidate.verified")] public sealed record CandidateVerified([property: Id(0)] string Sha, [property: Id(1)] VerificationReport Report) : Signal;
[GenerateSerializer, Alias("aspire.candidate.promoted")] public sealed record CandidatePromoted([property: Id(0)] string Sha) : Signal;
[GenerateSerializer, Alias("aspire.candidate.failed")] public sealed record CandidateFailed([property: Id(0)] string Sha, [property: Id(1)] CandidateStage Stage, [property: Id(2)] string Reason) : Signal;
