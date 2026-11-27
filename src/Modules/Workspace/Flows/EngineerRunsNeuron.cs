using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Flows;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices;
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

        var runKey = string.IsNullOrWhiteSpace(request.RunId)
            ? Guid.NewGuid().ToString("N")[..12]
            : request.RunId.Trim();

        // Tier 1 ($0 Azure compute): route to an eligible online Paired PC advertising AllowConnectedEngineering == true
        // with pauseAtApprovalGate: true so Cloud Ino receives a SHA-locked ChangeSet + EngineerVerificationReceipt
        // for human Draft PR approval. Cleanly falls back to local AgentDevSlots (Tier 2) or Cloud ACA Job (Tier 3)
        // when no eligible Paired PC is online or consent is disabled.
        if (await TryRunOnConnectedPcAsync(request, runKey, repoSlug, goal, instances, ct).ConfigureAwait(true) is { } connectedSummary)
        {
            return connectedSummary;
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

    private async Task<EngineerRunSummary?> TryRunOnConnectedPcAsync(
        EngineerDelegationRequest request,
        string runKey,
        string repoSlug,
        string goal,
        int instances,
        CancellationToken ct)
    {
        if (services?.GetService<IConnectedComputeRouter>() is not { } computeRouter)
        {
            return null;
        }

        ComputeRouteDecision route;
        try
        {
            route = await computeRouter.ResolveEngineeringRouteAsync(Id.Brain, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"resolving Tier 1 Connected PC engineering route for brain '{Id.Brain.Value}'");
            return null;
        }

        if (route.Target != ComputeExecutionTarget.ConnectedClient
            || route.Node is not { IsEligibleForConnectedEngineering: true } pairedPc)
        {
            return null;
        }

        var slicesJson = request.Slices is { Length: > 0 }
            ? JsonSerializer.Serialize(request.Slices, JsonSerializerOptions.Web)
            : null;
        var engineeringRequest = new ConnectedEngineeringRequest(
            JobId: $"eng-{runKey}",
            BrainId: Id.Brain.Value,
            RunId: runKey,
            RepoSlug: repoSlug,
            Goal: goal,
            ParallelInstances: instances,
            RequestedBaseBranch: request.RequestedBaseBranch,
            RequirePlanRatification: request.RequirePlanRatification,
            Harness: request.Harness,
            CleanupTargets: request.CleanupTargets,
            SlicesJson: slicesJson,
            PauseAtApprovalGate: true,
            TraceParent: Activity.Current?.Id);

        ConnectedEngineeringResponse? remote;
        try
        {
            remote = await computeRouter
                .TryExecuteEngineeringOnConnectedClientAsync(Id.Brain, engineeringRequest, ct: ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger().LogSwallowed(ex, $"dispatching Plane 2 engineering run '{runKey}' to Paired PC '{pairedPc.MachineId}'; falling back");
            return null;
        }

        if (remote is null)
        {
            return null;
        }

        // If the Paired PC revoked consent right before execution, fall back cleanly to Tier 2 / Tier 3.
        if (!remote.Ok
            && !string.IsNullOrWhiteSpace(remote.Error)
            && remote.Error.StartsWith("consent-disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var flowId = !string.IsNullOrWhiteSpace(remote.FlowId)
            ? remote.FlowId
            : new NeuronId(Id.Brain, IEngineerRuns.EngineerFlow, runKey).ToString();

        if (!remote.Ok)
        {
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                repoSlug,
                goal,
                instances,
                "ConnectedPC",
                remote.Error ?? $"Connected PC '{pairedPc.DeviceName}' failed engineering run '{runKey}'.",
                flowId,
                request.Harness);
        }

        if (!remote.PausedAtApprovalGate
            || !string.Equals(remote.RunId?.Trim(), runKey, StringComparison.Ordinal)
            || remote.ChangeSet is null
            || remote.Receipt is null
            || !IsValidFullHexCommitSha(remote.ChangeSet.CommitSha)
            || !string.Equals(remote.ChangeSet.CommitSha, remote.Receipt.CommitSha, StringComparison.Ordinal)
            || !remote.Receipt.BuildVerified
            || !remote.Receipt.LiveVerified
            || !string.IsNullOrWhiteSpace(remote.Receipt.PushedRef)
            || !string.IsNullOrWhiteSpace(remote.Receipt.PullRequestUrl))
        {
            return EngineerDelegationSummaries.Failure(
                Id.Brain,
                repoSlug,
                goal,
                instances,
                "ConnectedPC",
                $"Connected PC '{pairedPc.DeviceName}' returned an unverified, mismatched, or policy-violating receipt for run '{runKey}'; fail-closed refusal.",
                flowId,
                request.Harness);
        }

        var changeSet = remote.ChangeSet;
        var receipt = remote.Receipt;
        var executedBy = string.IsNullOrWhiteSpace(remote.ExecutedByMachineId)
            ? pairedPc.MachineId
            : remote.ExecutedByMachineId;
        var tasksKey = IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "tasks").Key;
        var tasksUri = $"neuron://{tasksKey}#{flowId}";
        var confirmationPlanId = $"app:engineer:{runKey}:{changeSet.CommitSha}";
        var shortSha = changeSet.CommitSha.Length >= 8 ? changeSet.CommitSha[..8] : changeSet.CommitSha;

        var instanceSummary = new EngineerInstanceSummary(
            InstanceIndex: 0,
            LeaseId: $"pc-{executedBy}-{runKey}",
            Backend: "ConnectedPC",
            BranchName: changeSet.BranchName,
            Status: "verified",
            CompletedTaskIds: [],
            ChangedFiles: changeSet.ChangedFiles ?? [],
            CommitSha: changeSet.CommitSha,
            BuildSucceeded: receipt.BuildVerified,
            LiveSucceeded: receipt.LiveVerified,
            FailureDetail: null,
            Strategy: "connected-pc",
            TotalScore: receipt.BuildVerified && receipt.LiveVerified ? 100 : 80,
            ChangedLines: changeSet.Additions + changeSet.Deletions,
            UnifiedDiff: changeSet.UnifiedDiff ?? string.Empty,
            Rank: 1,
            IsSelected: true,
            Harness: request.Harness);

        var summaryText = !string.IsNullOrWhiteSpace(remote.SummaryText)
            ? remote.SummaryText
            : string.Create(
                CultureInfo.InvariantCulture,
                $"IntoChat-Engineer run `{flowId}` executed on Paired PC `{pairedPc.DeviceName}` (`{executedBy}`, $0 Azure compute), verified commit `{changeSet.CommitSha}` ({(changeSet.ChangedFiles ?? []).Length} file(s)), and paused at `approval-gate` for human Draft PR approval.");

        var approvalCard = new InoCard(
            Id: confirmationPlanId,
            Kind: "approval",
            Title: $"Engineer ready for approval (Paired PC): {goal} ({shortSha})",
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"Verified on Paired PC {pairedPc.DeviceName} ($0 Azure compute) · {(changeSet.ChangedFiles ?? []).Length} file(s) on {changeSet.BranchName}."),
            SourceNeuronUri: tasksUri,
            Status: "pending",
            SurfaceScopeUri: tasksUri);

        return new EngineerRunSummary(
            Ok: true,
            FlowId: flowId,
            RunId: runKey,
            RepoSlug: repoSlug,
            Goal: goal,
            Backend: "ConnectedPC",
            ParallelInstances: instances,
            Status: FlowStatus.Waiting,
            CurrentStage: "approval-gate",
            CompletedStages: ["brainstorm-and-plan", "prepare-worktree", "implement-tasks", "build-and-test", "live-verify"],
            ConfirmationPlanId: confirmationPlanId,
            TraceId: Activity.Current?.TraceId.ToString(),
            WinningCommitSha: changeSet.CommitSha,
            PushedRef: null,
            PullRequestUrl: null,
            EngineerAppUri: tasksUri,
            TasksBoardUri: tasksUri,
            Plan: null,
            Instances: [instanceSummary],
            DiagnosticsAndTraces: remote.DiagnosticsAndTraces ?? [route.Reason],
            SummaryText: summaryText,
            Card: approvalCard,
            SelectedCandidateIndex: 0,
            Harness: request.Harness,
            Receipt: receipt);
    }

    private static bool IsValidFullHexCommitSha(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 40)
        {
            return false;
        }

        if (!CommitSha.TryParse(value, out _))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
