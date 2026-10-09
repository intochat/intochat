using DigitalBrain.Framework;
using DigitalBrain.Revisions.Signals;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace DigitalBrain.Revisions;

[GrainType("revisions.revision")]
internal sealed class RevisionNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<RevisionState> store,
    ILauncherClient launcher,
    IRevisionVerifier verifier,
    IOptions<RevisionsOptions> options)
    : Neuron<RevisionState>(store), IRevision
{
    private string Key => this.GetPrimaryKeyString();
    private string Sha => Key[(Key.LastIndexOf('/') + 1)..];
    private string BrainId => BrainKey.BrainOf(Key);
    private IRevisions Index => GrainFactory.GetGrain<IRevisions>(BrainId);

    public Task<RevisionSnapshot> Read() => Task.FromResult(Describe());

    public async Task<RevisionSnapshot> Propose(string branch, string worktreePath, string summary, VerificationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(plan);
        if (Exists && Snapshot.History.Length > 0) { throw new InvalidOperationException($"Revision {Sha} is already {Snapshot.Stage}."); }
        await Move(RevisionStage.Proposed, new RevisionState { Branch = branch, Worktree = worktreePath, Summary = summary, Plan = plan }, new RevisionBuilding(Sha));
        return Describe();
    }

    public async Task<RevisionSnapshot> Queue()
    {
        Require(RevisionStage.Proposed);
        await Move(RevisionStage.Queued, Snapshot, null);
        return Describe();
    }

    public async Task<RevisionSnapshot> Start()
    {
        Require(RevisionStage.Proposed, RevisionStage.Queued);
        await Move(RevisionStage.Starting, Snapshot, null);
        try
        {
            var started = await launcher.Start(Sha, CancellationToken.None);
            await Move(RevisionStage.Started, Snapshot with { McpEndpoint = started.McpEndpoint }, new RevisionStarted(Sha, started.McpEndpoint));
            await launcher.WaitHealthy(Sha, options.Value.StartBudget, CancellationToken.None);
            await Move(RevisionStage.Healthy, Snapshot, new RevisionHealthy(Sha));
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or HttpRequestException)
        {
            await Fail(error.Message);
        }
        return Describe();
    }

    public async Task<RevisionSnapshot> Verify(VerificationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Require(RevisionStage.Healthy);
        var endpoint = Snapshot.McpEndpoint ?? throw new InvalidOperationException($"Revision {Sha} has no MCP endpoint.");
        await Move(RevisionStage.Verifying, Snapshot with { Plan = plan }, null);
        using var budget = new CancellationTokenSource(plan.Budget);
        VerificationReport report;
        try { report = await verifier.Verify(BrainId, endpoint, plan, budget.Token); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            await Fail($"budget {plan.Budget} exceeded");
            return Describe();
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException)
        {
            await Fail(error.Message);
            return Describe();
        }
        if (report.Passed) { await Move(RevisionStage.Verified, Snapshot with { Report = report }, new RevisionVerified(Sha, report)); }
        else
        {
            var failed = report.Checks.First(check => !check.Passed);
            await Fail($"{failed.Name}: {failed.Detail}", Snapshot with { Report = report });
        }
        return Describe();
    }

    public async Task<RevisionSnapshot> Promote()
    {
        if (Snapshot.Stage != RevisionStage.Verified || !Snapshot.History.Contains(RevisionStage.Verified))
        { throw new InvalidOperationException($"Revision {Sha} is {Snapshot.Stage}; only a Verified revision can be promoted."); }
        await launcher.Promote(Sha, CancellationToken.None);
        await Move(RevisionStage.Promoted, Snapshot, new RevisionPromoted(Sha));
        await Index.Ended(Sha);
        return Describe();
    }

    public async Task<RevisionSnapshot> Discard()
    {
        if (Snapshot.Stage is RevisionStage.Promoted or RevisionStage.Discarded) { return Describe(); }
        if (Snapshot.McpEndpoint is not null) { await launcher.Discard(Sha, CancellationToken.None); }
        await Move(RevisionStage.Discarded, Snapshot, null);
        await Index.Ended(Sha);
        return Describe();
    }

    private void Require(params RevisionStage[] stages)
    {
        if (!stages.Contains(Snapshot.Stage)) { throw new InvalidOperationException($"Revision {Sha} is {Snapshot.Stage}; expected {string.Join(" or ", stages)}."); }
    }

    // The failed signal names the stage the revision was in, so a report reads "Verify failed", not "Failed failed".
    private async Task Fail(string reason, RevisionState? from = null)
    {
        var failedAt = Snapshot.Stage;
        await Move(RevisionStage.Failed, (from ?? Snapshot) with { Failure = reason }, new RevisionFailed(Sha, failedAt, reason));
        await Index.Ended(Sha);
    }

    private Task Move(RevisionStage stage, RevisionState next, Signal? acted)
        => Save(next with { Stage = stage, History = [.. next.History, stage] }, new RevisionChanged(Sha, stage), acted);

    private RevisionSnapshot Describe()
        => new(Sha, Snapshot.Branch, Snapshot.Worktree, Snapshot.Summary, Snapshot.Stage, Snapshot.McpEndpoint, Snapshot.Plan, Snapshot.Report, Snapshot.Failure, Snapshot.History);
}