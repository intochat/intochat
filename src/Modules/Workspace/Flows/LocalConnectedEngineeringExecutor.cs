using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Identity.Local.Machine;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Paired-PC local Plane 2 self-improving engineering executor (INT-2599 Slice 2).
/// Executes planning -> isolated git worktree -> slice edits -> build/test -> real
/// <c>AgentDevSlotsCandidateHost</c> + <c>CandidateLiveVerifier</c> (MCP + Playwright + OTel)
/// locally on the user's Desktop PC, streaming NDJSON <see cref="EngineeringStreamChunk"/> progress
/// back to Cloud and always pausing at <c>approval-gate</c> (<c>PausedAtApprovalGate = true</c>)
/// to return a SHA-locked <see cref="ConnectedEngineeringChangeSet"/> and <see cref="EngineerVerificationReceipt"/>
/// for human Draft PR approval in Cloud Ino ($0 Azure compute).
/// </summary>
internal sealed class LocalConnectedEngineeringExecutor(
    IWorkTree workTree,
    IEngineerPlanner planner,
    IEngineerBuildVerifier buildVerifier,
    IEngineeringHarness? harness = null,
    ICoder? coder = null,
    IEngineerCandidateHostVerifier? candidateHostVerifier = null,
    ILiveVerifier? liveVerifier = null,
    MachineId? thisMachine = null,
    ILogger<LocalConnectedEngineeringExecutor>? logger = null,
    TimeProvider? clock = null) : IStreamingLocalEngineeringExecutor
{
    private static readonly string[] DefaultAllowedExtensions =
    [
        ".cs", ".csproj", ".props", ".targets", ".json", ".md", ".txt", ".yml", ".yaml", ".dart", ".ts", ".tsx",
    ];

    private const int DefaultMaxChangedFiles = 24;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<LocalConnectedEngineeringExecutor>();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ConnectedEngineeringResponse> ExecuteAsync(
        ConnectedEngineeringRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ConnectedEngineeringResponse? terminal = null;
        string? lastError = null;

        await foreach (var chunk in ExecuteStreamingAsync(request, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Error))
            {
                lastError = chunk.Error;
            }

            if (chunk.Result is not null)
            {
                terminal = chunk.Result;
            }
        }

        return terminal ?? new ConnectedEngineeringResponse(
            Ok: false,
            RunId: request.RunId,
            Error: lastError ?? "Connected engineering execution completed without a terminal result.",
            ExecutedByMachineId: thisMachine?.Value);
    }

    public async IAsyncEnumerable<EngineeringStreamChunk> ExecuteStreamingAsync(
        ConnectedEngineeringRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? Guid.NewGuid().ToString("N")[..12]
            : request.RunId.Trim();
        var repoSlug = string.IsNullOrWhiteSpace(request.RepoSlug) ? "os" : request.RepoSlug.Trim();
        var goal = request.Goal?.Trim() ?? string.Empty;
        var machineId = thisMachine?.Value;
        var diagnostics = new List<string>();
        var executionTimeoutSeconds = Math.Clamp(request.TimeoutSeconds, 15, 900);
        var executionTimeout = TimeSpan.FromSeconds(executionTimeoutSeconds);

        using var timeoutCts = new CancellationTokenSource(executionTimeout, _clock);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var execCt = linkedCts.Token;

        WorkTreeLease? lease = null;
        var succeeded = false;

        try
        {
            if (string.IsNullOrWhiteSpace(goal))
            {
                var invalid = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "brainstorm-and-plan",
                    Error: "Connected engineering requires a non-empty goal.",
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "brainstorm-and-plan", Done: true, Error: invalid.Error, Result: invalid);
                yield break;
            }

            RepositoryId? parsedRepo = null;
            BranchName? parsedBase = null;
            string? repoResolveError = null;
            try
            {
                parsedRepo = RepositoryId.Parse(repoSlug);
                parsedBase = string.IsNullOrWhiteSpace(request.RequestedBaseBranch)
                    ? null
                    : BranchName.FromRequestedBase(request.RequestedBaseBranch);
                _ = workTree.ResolveRepository(parsedRepo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not resolve repository '{RepoSlug}' on Paired PC.", repoSlug);
                repoResolveError = $"Could not resolve repository '{repoSlug}' on Paired PC: {ex.Message}";
            }

            if (repoResolveError is not null || parsedRepo is null)
            {
                var err = repoResolveError ?? $"Could not resolve repository '{repoSlug}' on Paired PC.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "brainstorm-and-plan",
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "brainstorm-and-plan", Done: true, Error: err, Result: failed);
                yield break;
            }

            // 1. Plan
            yield return new EngineeringStreamChunk(
                Stage: "brainstorm-and-plan",
                Progress: $"Planning '{goal}' on Paired PC for repository '{parsedRepo.Value}'...");

            EngineerTaskPlan? plan = null;
            string? planError = null;
            try
            {
                plan = await planner.PlanAsync(
                    parsedRepo.Value,
                    goal,
                    DefaultAllowedExtensions,
                    DefaultMaxChangedFiles,
                    execCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Planning timed out on Paired PC for run '{RunId}' after {TimeoutSeconds}s.", runId, executionTimeoutSeconds);
                planError = $"timeout: Planning exceeded {executionTimeoutSeconds}s execution cap on Paired PC.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Planning failed on Paired PC for run '{RunId}'.", runId);
                planError = $"Planning failed on Paired PC: {ex.Message}";
            }

            if (planError is not null || plan is null)
            {
                var err = planError ?? "Planning returned no plan on Paired PC.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "brainstorm-and-plan",
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "brainstorm-and-plan", Done: true, Error: err, Result: failed);
                yield break;
            }

            var slices = TryDeserializeSlices(request.SlicesJson) ?? EngineerTaskSlices.FromTaskPlan(plan);
            diagnostics.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"plan.connected-pc · {plan.Tasks.Length} task(s), {slices.Length} slice(s), harness={request.Harness}"));

            // 2. Isolated git worktree
            yield return new EngineeringStreamChunk(
                Stage: "prepare-worktree",
                Progress: $"Preparing isolated git worktree for run '{runId}'...");

            string? worktreeError = null;
            try
            {
                lease = await workTree.PrepareAsync(parsedRepo, runId, goal, parsedBase, execCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Worktree preparation timed out on Paired PC for run '{RunId}' after {TimeoutSeconds}s.", runId, executionTimeoutSeconds);
                worktreeError = $"timeout: Worktree preparation exceeded {executionTimeoutSeconds}s execution cap on Paired PC.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Worktree preparation failed on Paired PC for run '{RunId}'.", runId);
                worktreeError = $"Worktree preparation failed on Paired PC: {ex.Message}";
            }

            if (worktreeError is not null || lease is null)
            {
                var err = worktreeError ?? "Worktree preparation returned no lease.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "prepare-worktree",
                    DiagnosticsAndTraces: [.. diagnostics],
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "prepare-worktree", Done: true, Error: err, Result: failed);
                yield break;
            }

            // 3. Implement tasks & produce SHA-locked ChangeSet
            yield return new EngineeringStreamChunk(
                Stage: "implement-tasks",
                Progress: $"Executing slice edits in isolated worktree '{lease.BranchName}'...");

            ChangeSet? changeSet = null;
            string? implementError = null;
            try
            {
                if (harness is not null)
                {
                    var ratifiedPlan = new RatifiedPlan(
                        Goal: goal,
                        ArchitectureSummary: plan.ArchitectureSummary,
                        CleanupTargets: request.CleanupTargets ?? plan.CleanupTargets ?? [],
                        Slices: slices,
                        Ratified: true,
                        RatifiedBy: "connected-pc-relay",
                        RatifiedAt: _clock.GetUtcNow(),
                        Harness: request.Harness,
                        CodeGraphDigest: plan.CodeGraphDigest);

                    var harnessRequest = new EngineeringHarnessRequest(
                        FlowId: $"connected-pc/{runId}",
                        RunId: runId,
                        RepositoryId: parsedRepo.Value,
                        Goal: goal,
                        WorktreeId: lease.EffectiveLeaseId,
                        WorktreePath: lease.TreePath,
                        InstanceIndex: 0,
                        TotalInstances: 1,
                        Strategy: "surgical-patch",
                        StrategyLabel: "Surgical Patch",
                        StrategyPromptLens: "Apply minimal, clean, well-tested changes.",
                        Harness: request.Harness,
                        RatifiedPlan: ratifiedPlan,
                        Brain: new DigitalBrain.BrainId(request.BrainId),
                        BaseBranch: lease.BaseBranch,
                        SourceRepositoryPath: lease.SourceRepoPath);

                    var harnessResult = await harness.ExecuteAsync(harnessRequest, ct: execCt).ConfigureAwait(false);
                    if (!harnessResult.Succeeded)
                    {
                        implementError = harnessResult.FailureDetail ?? "Engineering harness reported failure on Paired PC.";
                    }
                }
                else if (coder is not null)
                {
                    _ = await coder.EditAsync(lease, goal, execCt).ConfigureAwait(false);
                }
                else
                {
                    implementError = "No IEngineeringHarness or ICoder is configured on this Paired PC.";
                }

                if (implementError is null)
                {
                    var commitMessage = $"ino: {goal}";
                    changeSet = await workTree.VerifyAndCommitAsync(lease, commitMessage, execCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Implementation timed out on Paired PC for run '{RunId}' after {TimeoutSeconds}s.", runId, executionTimeoutSeconds);
                implementError = $"timeout: Implementation exceeded {executionTimeoutSeconds}s execution cap on Paired PC.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Implementation/commit failed on Paired PC for run '{RunId}'.", runId);
                implementError = $"Implementation/commit failed on Paired PC: {ex.Message}";
            }

            if (implementError is not null || changeSet is null)
            {
                var err = implementError ?? "No verified commit produced on Paired PC.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "implement-tasks",
                    DiagnosticsAndTraces: [.. diagnostics],
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "implement-tasks", Done: true, Error: err, Result: failed);
                yield break;
            }

            var changedFiles = changeSet.ChangedFiles.Select(static f => f.Value).ToArray();
            var commitSha = changeSet.Commit.Value;
            diagnostics.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"commit.sha-locked · {commitSha} ({changedFiles.Length} file(s), +{changeSet.Additions}/-{changeSet.Deletions})"));

            // 4. Build & test gate
            yield return new EngineeringStreamChunk(
                Stage: "build-and-test",
                Progress: $"Running build and test verification for commit {ShortSha(commitSha)}...");

            EngineerBuildReport? buildReport = null;
            string? buildError = null;
            try
            {
                buildReport = await buildVerifier.VerifyAsync(lease.TreePath, changedFiles, execCt).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(buildReport.Summary))
                {
                    diagnostics.Add(buildReport.Summary);
                }

                if (!buildReport.Succeeded)
                {
                    buildError = buildReport.Summary ?? "Build verification failed on Paired PC.";
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Build verification timed out on Paired PC for run '{RunId}' after {TimeoutSeconds}s.", runId, executionTimeoutSeconds);
                buildError = $"timeout: Build verification exceeded {executionTimeoutSeconds}s execution cap on Paired PC.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Build verification threw on Paired PC for run '{RunId}'.", runId);
                buildError = $"Build verification threw on Paired PC: {ex.Message}";
            }

            if (buildError is not null || buildReport is not { Succeeded: true })
            {
                var err = buildError ?? "Build verification failed on Paired PC.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "build-and-test",
                    DiagnosticsAndTraces: [.. diagnostics],
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "build-and-test", Done: true, Error: err, Result: failed);
                yield break;
            }

            // 5. Live verification via AgentDevSlotsCandidateHost + CandidateLiveVerifier (MCP + Playwright + OTel).
            // Fail closed when no live verifier is configured on this Paired PC.
            var hasCandidateVerifier = candidateHostVerifier is { IsConfigured: true };
            var hasLiveVerifier = liveVerifier is not null;
            if (!hasCandidateVerifier && !hasLiveVerifier)
            {
                const string unconfiguredError = "live-verify-unconfigured: No live verifier (CandidateLiveVerifier or ILiveVerifier) is configured on this Paired PC; fail-closed refusal.";
                diagnostics.Add(unconfiguredError);
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "live-verify",
                    DiagnosticsAndTraces: [.. diagnostics],
                    Error: unconfiguredError,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "live-verify", Done: true, Error: unconfiguredError, Result: failed);
                yield break;
            }

            var hostKind = hasCandidateVerifier
                ? candidateHostVerifier!.HostKind
                : "LocalLiveVerifier";
            yield return new EngineeringStreamChunk(
                Stage: "live-verify",
                Progress: $"Running live verification ({hostKind}) for commit {ShortSha(commitSha)}...");

            EngineerLiveReport? liveReport = null;
            string? liveError = null;
            var touchesUi = changedFiles.Any(static f =>
                f.EndsWith(".dart", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
                || f.Contains("Apps/", StringComparison.OrdinalIgnoreCase)
                || f.Contains("UI/", StringComparison.OrdinalIgnoreCase));

            try
            {
                liveReport = hasCandidateVerifier
                    ? await candidateHostVerifier!.VerifyCandidateAsync(
                        new CandidateLiveVerificationRequest(
                            WorktreePath: lease.TreePath,
                            Goal: goal,
                            ChangedFiles: changedFiles,
                            CommitSha: commitSha,
                            TouchesUi: touchesUi,
                            BrainId: request.BrainId),
                        execCt).ConfigureAwait(false)
                    : await liveVerifier!.VerifyLiveAsync(
                        lease.TreePath,
                        goal,
                        changedFiles,
                        execCt).ConfigureAwait(false);

                if (liveReport is not null)
                {
                    if (!string.IsNullOrWhiteSpace(liveReport.Summary))
                    {
                        diagnostics.Add(liveReport.Summary);
                    }

                    foreach (var trace in liveReport.TraceSummaries ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(trace))
                        {
                            diagnostics.Add(trace);
                        }
                    }

                    if (!liveReport.Succeeded)
                    {
                        liveError = liveReport.Summary ?? "Live verification failed on Paired PC.";
                    }
                }
                else
                {
                    liveError = "Live verification returned no report on Paired PC; fail-closed refusal.";
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Live verification timed out on Paired PC for run '{RunId}' after {TimeoutSeconds}s.", runId, executionTimeoutSeconds);
                liveError = $"timeout: Live verification exceeded {executionTimeoutSeconds}s execution cap on Paired PC.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Live verification threw on Paired PC for run '{RunId}'.", runId);
                liveError = $"Live verification threw on Paired PC: {ex.Message}";
            }

            if (liveError is not null || liveReport is not { Succeeded: true })
            {
                var err = liveError ?? "Live verification failed on Paired PC.";
                var failed = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: runId,
                    Stage: "live-verify",
                    DiagnosticsAndTraces: [.. diagnostics],
                    Error: err,
                    ExecutedByMachineId: machineId);
                yield return new EngineeringStreamChunk(Stage: "live-verify", Done: true, Error: err, Result: failed);
                yield break;
            }

            // 6. Always pause at approval-gate: NEVER push or open PR from the remote PC relay,
            // and hardcode PausedAtApprovalGate = true regardless of request payload.
            var wireChangeSet = new ConnectedEngineeringChangeSet(
                CommitSha: commitSha,
                BaseSha: lease.BaseCommitSha,
                BranchName: lease.BranchName,
                Repository: parsedRepo.Value,
                ChangedFiles: changedFiles,
                Additions: changeSet.Additions,
                Deletions: changeSet.Deletions,
                UnifiedDiff: changeSet.HostRenderedDiff);

            var receipt = new EngineerVerificationReceipt(
                CommitSha: commitSha,
                PullRequestUrl: null,
                PushedRef: null,
                BuildVerified: true,
                LiveVerified: true,
                CandidateHostKind: $"ConnectedPC/{hostKind}",
                VerificationSummary: liveReport.Summary ?? buildReport.Summary);

            var summaryText = string.Create(
                CultureInfo.InvariantCulture,
                $"Paired PC verified commit `{commitSha}` ({changedFiles.Length} file(s), +{changeSet.Additions}/-{changeSet.Deletions}) via `{hostKind}` ($0 Azure compute) and paused at `approval-gate` for human Draft PR approval.");

            var response = new ConnectedEngineeringResponse(
                Ok: true,
                RunId: runId,
                FlowId: $"connected-pc/{runId}",
                Stage: "approval-gate",
                PausedAtApprovalGate: true,
                ChangeSet: wireChangeSet,
                Receipt: receipt,
                DiagnosticsAndTraces: [.. diagnostics],
                SummaryText: summaryText,
                Error: null,
                ExecutedByMachineId: machineId);

            succeeded = true;
            yield return new EngineeringStreamChunk(
                Stage: "approval-gate",
                Progress: summaryText,
                Done: true,
                Result: response);
        }
        finally
        {
            if (!succeeded && lease is not null)
            {
                await CleanupBestEffortAsync(lease).ConfigureAwait(false);
            }
        }
    }

    private static EngineerTaskSlice[]? TryDeserializeSlices(string? slicesJson)
    {
        if (string.IsNullOrWhiteSpace(slicesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EngineerTaskSlice[]>(slicesJson, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ShortSha(string sha)
        => sha.Length > 8 ? sha[..8] : sha;

    private async Task CleanupBestEffortAsync(WorkTreeLease lease)
    {
        try
        {
            using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
            await workTree.CleanupAsync(lease, cleanupCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Best-effort worktree cleanup failed on Paired PC for run '{RunId}'.", lease.RunId);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "Best-effort worktree cleanup timed out on Paired PC for run '{RunId}'.", lease.RunId);
        }
    }
}
