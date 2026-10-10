using DigitalBrain.Framework;
using DigitalBrain.Revisions.Signals;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Revisions;

// At most one revision is live per brain; the rest wait here in proposal order.
[GrainType("revisions.index")]
internal sealed class RevisionIndexNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<RevisionIndexState> store,
    ILauncherClient launcher)
    : Neuron<RevisionIndexState>(store), IRevisions, IRevisionIndexWork
{
    private string BrainId => this.GetPrimaryKeyString();

    public async Task<string> Propose(RevisionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var committed = await launcher.Propose(proposal.Branch, proposal.Summary, proposal.Files, CancellationToken.None);
        var revision = GrainFactory.GetGrain<IRevision>(IRevision.Key(BrainId, committed.Sha));
        await revision.Propose(proposal.Branch, proposal.Summary, proposal.Plan);
        if (Snapshot.Live is null)
        {
            await Save(Snapshot with { Live = committed.Sha }, new RevisionChanged(committed.Sha, RevisionStage.Starting));
            // One-way through a self reference: Start outlives the call and must not hold the index.
            await this.AsReference<IRevisionIndexWork>().StartLive(committed.Sha);
        }
        else
        {
            await Save(Snapshot with { Queue = [.. Snapshot.Queue, committed.Sha] }, new RevisionChanged(committed.Sha, RevisionStage.Queued));
            await revision.Queue();
        }
        return committed.Sha;
    }

    public Task<string?> Live() => Task.FromResult(Snapshot.Live);

    public async Task Ended(string sha)
    {
        if (Snapshot.Live != sha) { return; }
        var next = Snapshot.Queue.FirstOrDefault();
        await Save(Snapshot with { Live = next, Queue = next is null ? [] : Snapshot.Queue[1..] }, new RevisionChanged(sha, RevisionStage.Discarded));
        if (next is not null) { await this.AsReference<IRevisionIndexWork>().StartLive(next); }
    }

    public Task StartLive(string sha) => GrainFactory.GetGrain<IRevision>(IRevision.Key(BrainId, sha)).Start();
}

[Alias("revisions.index-work"), Orleans.Metadata.DefaultGrainType("revisions.index")]
internal interface IRevisionIndexWork : IGrainWithStringKey
{
    [OneWay] Task StartLive(string sha);
}