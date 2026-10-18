namespace IntoChat.OS.Microsoft.Aspire.Candidates;

[GenerateSerializer, Alias("aspire.candidates.state")]
internal sealed record CandidateQueueState
{
    [Id(0)] public string? Live { get; init; }
    [Id(1)] public string[] Queue { get; init; } = [];
}
