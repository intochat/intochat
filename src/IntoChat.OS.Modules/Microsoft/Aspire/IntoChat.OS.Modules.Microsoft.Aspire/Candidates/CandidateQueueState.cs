using DigitalBrain.State;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

[GenerateSerializer, Alias("aspire.candidates.state")]
[ExportableState]
internal sealed record CandidateQueueState
{
    [Id(0)] public string? Live { get; init; }
    [Id(1)] public string[] Queue { get; init; } = [];
}
