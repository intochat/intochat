using DigitalBrain.Framework;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Candidates;

// At most one candidate is live per brain; the rest wait here in proposal order.
[GrainType("aspire.candidates")]
internal sealed class CandidateQueueNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CandidateQueueState> store,
    ICandidateHost host)
    : Neuron<CandidateQueueState>(store), ICandidates
{
    private string BrainId => BrainKey.BrainOf(this.GetPrimaryKeyString());

    public async Task<string> Propose(CandidateProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var cluster = await GrainFactory.GetGrain<ICluster>(ICluster.Key(BrainId)).Read();
        var prepared = await host.Prepare(cluster.CurrentSha, proposal.Summary, proposal.Files, CancellationToken.None);
        var candidate = GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, prepared.Sha));
        await candidate.Propose(proposal.Branch, proposal.Summary, proposal.Plan);
        if (Snapshot.Live is null)
        {
            await Save(Snapshot with { Live = prepared.Sha }, new CandidateChanged(prepared.Sha, CandidateStage.Building));
            await candidate.Begin();
        }
        else
        {
            await Save(Snapshot with { Queue = [.. Snapshot.Queue, prepared.Sha] }, new CandidateChanged(prepared.Sha, CandidateStage.Queued));
            await candidate.Queue();
        }
        return prepared.Sha;
    }

    public Task<string?> Live() => Task.FromResult(Snapshot.Live);

    public Task<string[]> Known() => Task.FromResult(Snapshot.Live is null ? Snapshot.Queue : [Snapshot.Live, .. Snapshot.Queue]);

    public async Task Ended(string sha)
    {
        if (Snapshot.Queue.Contains(sha))
        {
            await Save(Snapshot with { Queue = [.. Snapshot.Queue.Where(queued => queued != sha)] }, new CandidateChanged(sha, CandidateStage.Discarded));
            return;
        }
        if (Snapshot.Live != sha) { return; }
        var next = Snapshot.Queue.FirstOrDefault();
        await Save(Snapshot with { Live = next, Queue = next is null ? [] : Snapshot.Queue[1..] }, new CandidateChanged(sha, CandidateStage.Discarded));
        if (next is not null) { await GrainFactory.GetGrain<ICandidate>(ICandidate.Key(BrainId, next)).Begin(); }
    }
}
