using DigitalBrain.State;

namespace IntoChat.Aspire.Candidates;

[ExportableState]
internal sealed record CandidateQueueState
{
    public string? Live { get; init; }
    public string[] Queue { get; init; } = [];
}
