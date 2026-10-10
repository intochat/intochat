using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire.Verification;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Microsoft.Aspire.Candidates;

[GrainType("aspire.candidate")]
internal sealed class CandidateNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CandidateState> store,
    ICandidateHost host,
    ICandidateVerifier verifier,
    ILogger<CandidateNeuron> log)
    : Neuron<CandidateState>(store), ICandidate
{
    private string Key => this.GetPrimaryKeyString();
    private string Sha => Key[(Key.LastIndexOf('/') + 1)..];
    private string BrainId => BrainKey.BrainOf(Key);
    private ICandidates Index => GrainFactory.GetGrain<ICandidates>(ICandidates.Key(BrainId));
    private ICluster LiveCluster => GrainFactory.GetGrain<ICluster>(ICluster.Key(BrainId));

    public Task<CandidateSnapshot> Read() => Task.FromResult(Describe());

    public async Task<CandidateSnapshot> Propose(string branch, string summary, VerificationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentNullException.ThrowIfNull(plan);
        if (Exists && Snapshot.History.Length > 0) { throw new InvalidOperationException($"Candidate {Sha} is already {Snapshot.Stage}."); }
        await Move(CandidateStage.Proposed, new CandidateState { Branch = branch, Summary = summary, Plan = plan }, null);
        return Describe();
    }

    public async Task<CandidateSnapshot> Queue()
    {
        Require(CandidateStage.Proposed);
        await Move(CandidateStage.Queued, Snapshot, null);
        return Describe();
    }

    public async Task<CandidateSnapshot> Start()
    {
        Require(CandidateStage.Proposed, CandidateStage.Queued);
        var plan = Snapshot.Plan!;
        try
        {
            await Move(CandidateStage.Building, Snapshot, null);
            await host.BuildGate(Sha, CancellationToken.None);
            await host.Publish(Sha, CancellationToken.None);
            await Move(CandidateStage.Starting, Snapshot, null);
            var running = await host.Run(Sha, CancellationToken.None);
            await Move(CandidateStage.Starting, Snapshot with { Endpoints = running.Endpoints }, new CandidateStarted(Sha, running.Endpoints));
            await host.WaitHealthy(Sha, plan.Budget, CancellationToken.None);
            await Move(CandidateStage.Healthy, Snapshot, new CandidateHealthy(Sha));
        }
        catch (Exception error)
        {
            await Fail(error.Message, removeContainer: Snapshot.Stage == CandidateStage.Starting);
        }
        return Describe();
    }

    public async Task<CandidateSnapshot> Verify()
    {
        Require(CandidateStage.Healthy);
        var plan = Snapshot.Plan!;
        await Move(CandidateStage.Verifying, Snapshot, null);
        using var budget = new CancellationTokenSource(plan.Budget);
        VerificationReport report;
        try { report = await verifier.Verify(BrainId, Sha, Snapshot.Endpoints!, plan, budget.Token); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            await Fail($"budget {plan.Budget} exceeded", removeContainer: true);
            return Describe();
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException)
        {
            await Fail(error.Message, removeContainer: true);
            return Describe();
        }
        if (report.Passed) { await Move(CandidateStage.Verified, Snapshot with { Report = report }, new CandidateVerified(Sha, report)); }
        else
        {
            var failed = report.Checks.First(check => !check.Passed);
            await Fail($"{failed.Name}: {failed.Detail}", removeContainer: true, Snapshot with { Report = report });
        }
        return Describe();
    }

    // One-way, so nobody observes a throw: anything Start could not turn into Failed is logged here.
    public async Task Begin()
    {
        try { await Start(); }
        catch (Exception error) { log.LogError(error, "Candidate {Candidate} could not start.", Key); }
    }

    public async Task<CandidateSnapshot> Promote()
    {
        if (Snapshot.Stage is CandidateStage.Promoting or CandidateStage.Promoted) { return Describe(); }
        if (Snapshot.Stage != CandidateStage.Verified) { throw new InvalidOperationException($"Candidate {Sha} is {Snapshot.Stage}; only a Verified candidate can be promoted."); }
        await Move(CandidateStage.Promoting, Snapshot, null);
        await host.Commit(Sha, Snapshot.Branch, CancellationToken.None);
        // The silo running this grain may die inside Restart; the cluster calls Finish once the new build is up.
        await LiveCluster.Restart(Sha);
        return await Finish();
    }

    public async Task<CandidateSnapshot> Finish()
    {
        if (Snapshot.Stage != CandidateStage.Promoting) { return Describe(); }
        await host.Remove(Sha, CancellationToken.None);
        await Move(CandidateStage.Promoted, Snapshot, new CandidatePromoted(Sha));
        await Index.Ended(Sha);
        return Describe();
    }

    public async Task<CandidateSnapshot> Discard()
    {
        if (Snapshot.Stage is CandidateStage.Promoted or CandidateStage.Discarded) { return Describe(); }
        if (Snapshot.Endpoints is not null) { await host.Remove(Sha, CancellationToken.None); }
        await Move(CandidateStage.Discarded, Snapshot, null);
        await Index.Ended(Sha);
        return Describe();
    }

    private void Require(params CandidateStage[] stages)
    {
        if (!stages.Contains(Snapshot.Stage)) { throw new InvalidOperationException($"Candidate {Sha} is {Snapshot.Stage}; expected {string.Join(" or ", stages)}."); }
    }

    // The failed signal names the stage the candidate was in.
    private async Task Fail(string reason, bool removeContainer, CandidateState? from = null)
    {
        var failedAt = Snapshot.Stage;
        try
        {
            if (removeContainer) { await host.Remove(Sha, CancellationToken.None); }
            await Move(CandidateStage.Failed, (from ?? Snapshot) with { Failure = reason }, new CandidateFailed(Sha, failedAt, reason));
        }
        finally { await Index.Ended(Sha); }
    }

    private Task Move(CandidateStage stage, CandidateState next, Signal? acted)
        => Save(next with { Stage = stage, History = [.. next.History, stage] }, new CandidateChanged(Sha, stage), acted);

    private CandidateSnapshot Describe()
        => new(Sha, Snapshot.Branch, Snapshot.Summary, Snapshot.Stage, Snapshot.Endpoints, Snapshot.Plan, Snapshot.Report, Snapshot.Failure, Snapshot.History);
}
