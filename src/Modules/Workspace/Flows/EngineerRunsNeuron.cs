using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

// The brain's door to engineering runs. It holds no state: a run is a flow.engineer flow, started through the flows index and
// read from the index and the flow's own head. Calls to the index and the flow carry this call's stamped caller on.
[Neuron("engineer.runs"), StatelessNeuron]
internal sealed partial class EngineerRunsNeuron(
    ILogger<EngineerRunsNeuron>? logger = null,
    IServiceProvider? services = null) : Neuron, IEngineerRuns
{
    private const string NoRunFound = "No IntoChat-Engineer (flow.engineer) run was found in this brain.";

    [Slot(Key = IFlows.DefaultKey)]
    private partial IFlows Flows { get; }

    [Slot(Required = false)]
    private partial SlotCollection<IFlowStarter> FlowHeads { get; }

    [Slot(Required = false)]
    private partial SlotCollection<IEngineerFlow> EngineerFlows { get; }

    public async Task<EngineerRunSummary> StartEngineerRun(EngineerDelegationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repoSlug = string.IsNullOrWhiteSpace(request.RepoSlug) ? "os" : request.RepoSlug.Trim();
        var goal = request.Goal?.Trim() ?? string.Empty;
        var instances = Math.Clamp(request.ParallelInstances <= 0 ? 1 : request.ParallelInstances, 1, EngineerRun.MaxParallelInstances);
        var backend = EngineerRun.NormalizeBackend(request.Backend) ?? nameof(WorkTreeBackendKind.LocalGit);

        if (string.IsNullOrWhiteSpace(goal))
        {
            return EngineerDelegationSummaries.Failure(Id.Brain, repoSlug, goal, instances, backend, "Engineer delegation requires a non-empty goal.");
        }

        RepositoryId parsedRepo;
        BranchName? parsedBase;
        try
        {
            parsedRepo = RepositoryId.Parse(repoSlug);
            parsedBase = string.IsNullOrWhiteSpace(request.RequestedBaseBranch)
                ? null
                : BranchName.FromRequestedBase(request.RequestedBaseBranch);
            if (services?.GetService<IWorkTree>() is { } workTree)
            {
                _ = workTree.ResolveRepository(parsedRepo);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"resolving repository '{repoSlug}' for Engineer delegation");
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                repoSlug,
                goal,
                instances,
                backend,
                $"Could not start IntoChat-Engineer on repository '{repoSlug}': {ex.Message}");
        }

        var runKey = string.IsNullOrWhiteSpace(request.RunId)
            ? Guid.NewGuid().ToString("N")[..12]
            : request.RunId.Trim();
        var title = goal.Length <= 56 ? goal : goal[..56];
        var startPayload = new EngineerStartRequest(
            RunId: runKey,
            Repository: parsedRepo.Value,
            Goal: goal,
            InstanceCount: instances,
            RequestedBaseBranch: parsedBase?.Value,
            Backend: backend,
            RequirePlanRatification: request.RequirePlanRatification,
            Harness: request.Harness,
            CleanupTargets: request.CleanupTargets,
            Slices: request.Slices,
            CircleBrainId: request.CircleBrainId,
            Verification: request.Verification).ToStartJson();

        string flowId;
        try
        {
            flowId = await Flows.Start(IEngineerRuns.EngineerFlow, title, parentFlowId: null, startPayload, ct, flowId: runKey).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"starting EngineerFlow '{title}'");
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                repoSlug,
                goal,
                instances,
                backend,
                $"Could not start IntoChat-Engineer '{title}': {ex.Message}",
                harness: request.Harness);
        }

        await AwaitEngineerStepSettlementAsync(flowId, ct).ConfigureAwait(true);
        return await InspectEngineerRun(flowId, ct).ConfigureAwait(true);
    }

    public async Task<EngineerRunSummary> InspectEngineerRun(string? flowId = null, CancellationToken ct = default)
    {
        var resolvedFlowId = await ResolveEngineerFlowId(flowId, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(resolvedFlowId))
        {
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                "os",
                string.Empty,
                1,
                nameof(WorkTreeBackendKind.LocalGit),
                NoRunFound);
        }

        try
        {
            var head = await Flow(resolvedFlowId).Head().ConfigureAwait(true);
            var view = await Flows.Read(resolvedFlowId, ct).ConfigureAwait(true);
            return await EngineerDelegationSummaries.BuildAsync(Id.Brain, resolvedFlowId, view, head, services, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"inspecting EngineerFlow '{resolvedFlowId}'");
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                "os",
                string.Empty,
                1,
                nameof(WorkTreeBackendKind.LocalGit),
                $"Could not inspect IntoChat-Engineer flow '{resolvedFlowId}': {ex.Message}",
                resolvedFlowId);
        }
    }

    public async Task<EngineerRunSummary> RatifyEngineerPlan(
        string flowId,
        string[]? approvedCleanupTargets = null,
        EngineerTaskSlice[]? slices = null,
        CancellationToken ct = default)
    {
        var resolvedFlowId = await ResolveEngineerFlowId(flowId, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(resolvedFlowId))
        {
            throw new KeyNotFoundException(NoRunFound);
        }

        var caller = Context.Caller ?? throw new UnauthorizedAccessException("An engineering plan is ratified only by a stamped caller.");
        var ratifier = CallerRules.ActsFor(caller) ?? caller.Actor switch
        {
            Actor.Module module => module.Id,
            Actor.Scheduler => "scheduler",
            _ => "kernel",
        };
        await Run(resolvedFlowId).RatifyPlan(approvedCleanupTargets, slices, ratifier).ConfigureAwait(true);

        await AwaitEngineerStepSettlementAsync(resolvedFlowId, ct).ConfigureAwait(true);
        return await InspectEngineerRun(resolvedFlowId, ct).ConfigureAwait(true);
    }

    public async Task<EngineerRunSummary> SelectEngineerCandidate(string flowId, int candidateIndex, CancellationToken ct = default)
    {
        var resolvedFlowId = await ResolveEngineerFlowId(flowId, ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(resolvedFlowId))
        {
            throw new KeyNotFoundException(NoRunFound);
        }

        await Run(resolvedFlowId).SelectCandidate(candidateIndex).ConfigureAwait(true);

        return await InspectEngineerRun(resolvedFlowId, ct).ConfigureAwait(true);
    }

    private async Task AwaitEngineerStepSettlementAsync(string flowId, CancellationToken ct)
    {
        // Poll briefly when steps are actively advancing so live agent/UI calls return the next settled stage summary directly,
        // while exiting quickly if the host scheduler defers flow execution until an explicit pump (e.g. TestOS.Idle).
        var deadline = Context.Time.GetUtcNow() + TimeSpan.FromSeconds(15);
        for (var attempt = 0; attempt < 80 && !ct.IsCancellationRequested; attempt++)
        {
            var head = await Flow(flowId).Head().ConfigureAwait(true);
            if (head.Status is FlowStatus.Waiting or FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled)
            {
                break;
            }

            if (attempt >= 3 && head.Done.Length == 0)
            {
                break;
            }

            if (Context.Time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(50, ct).ConfigureAwait(true);
        }
    }

    // No id means the brain's most recently started engineering run, as the flows index lists it.
    private async Task<string?> ResolveEngineerFlowId(string? rawFlowId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawFlowId))
        {
            var latest = await Flows.List(new FlowQuery(Type: IEngineerRuns.EngineerFlow, Limit: 1), ct).ConfigureAwait(true);
            return latest.FirstOrDefault()?.FlowId;
        }

        var trimmed = rawFlowId.Trim();
        var hashIdx = trimmed.LastIndexOf('#');
        if (hashIdx >= 0 && hashIdx + 1 < trimmed.Length)
        {
            trimmed = trimmed[(hashIdx + 1)..];
        }

        if (NeuronId.TryParse(trimmed, out var parsed))
        {
            return parsed.Brain == Id.Brain ? parsed.ToString() : null;
        }

        return new NeuronId(Id.Brain, IEngineerRuns.EngineerFlow, trimmed).ToString();
    }

    private IFlowStarter Flow(string flowId) => FlowHeads[FlowNeuron(flowId)];

    private IEngineerFlow Run(string flowId) => EngineerFlows[FlowNeuron(flowId)];

    private NeuronId FlowNeuron(string flowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        if (!NeuronId.TryParse(flowId, out var id) || id.Brain != Id.Brain) { throw new ArgumentException($"{flowId} is not a flow of this brain.", nameof(flowId)); }
        return id;
    }

    private ILogger Logger() => (ILogger?)logger ?? OsConsoleLog.For<EngineerRunsNeuron>();
}
