using IntoChat.OS;
using IntoChat.OS.Framework;
using Orleans.Runtime;

namespace IntoChat.OS.Flutter.Workspace;

[GrainType("ui.problem-reports")]
internal sealed class ProblemReportsNeuron(
    [PersistentState("problem-reports", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ProblemReportsState> store)
    : Neuron, IProblemReports
{
    private const int MaxMessageLength = 4_000;

    public async Task<ProblemReport> Add(string workspaceId, string intentId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaxMessageLength)
        {
            throw new ArgumentException($"A problem report is limited to {MaxMessageLength} characters.", nameof(message));
        }

        var report = new ProblemReport(intentId, workspaceId, message, DateTimeOffset.UtcNow);
        var previous = store.State.Reports;
        store.State = store.State with { Reports = [.. previous, report] };
        try { await store.WriteStateAsync(); }
        catch { store.State = store.State with { Reports = previous }; throw; }
        return report;
    }

    public Task<IReadOnlyList<ProblemReport>> Read() => Task.FromResult<IReadOnlyList<ProblemReport>>([.. store.State.Reports]);
}
