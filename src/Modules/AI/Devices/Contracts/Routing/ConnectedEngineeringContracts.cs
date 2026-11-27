using DigitalBrain;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Plane 2 connected-PC self-improving engineering job dispatched from Cloud <c>EngineerRunsNeuron</c>
/// over <c>/circle/v1/compute/engineering/dispatch</c> and polled by an enrolled Desktop PC via
/// <c>/circle/v1/compute/engineering/next</c>.
/// Contains zero cluster custody keys or host secrets; always pauses at <c>approval-gate</c>
/// (<see cref="PauseAtApprovalGate"/> defaults to <see langword="true"/>) so the cloud orchestrator receives a
/// SHA-locked <see cref="ConnectedEngineeringChangeSet"/> and <see cref="EngineerVerificationReceipt"/>
/// for human Draft PR approval.
/// </summary>
[GenerateSerializer, Alias("devices.compute.engineering-request")]
public sealed record ConnectedEngineeringRequest(
    [property: Id(0)] string JobId,
    [property: Id(1)] string BrainId,
    [property: Id(2)] string RunId,
    [property: Id(3)] string RepoSlug,
    [property: Id(4)] string Goal,
    [property: Id(5)] int ParallelInstances = 1,
    [property: Id(6)] string? RequestedBaseBranch = null,
    [property: Id(7)] bool RequirePlanRatification = false,
    [property: Id(8)] EngineeringHarnessKind Harness = EngineeringHarnessKind.InoNative,
    [property: Id(9)] string[]? CleanupTargets = null,
    [property: Id(10)] string? SlicesJson = null,
    [property: Id(11)] bool PauseAtApprovalGate = true,
    [property: Id(12)] string? TraceParent = null,
    [property: Id(13)] int TimeoutSeconds = 900);

/// <summary>
/// SHA-locked git change set returned by a Paired PC after local worktree isolation, build/test,
/// and live verification (<c>AgentDevSlotsCandidateHost</c> + <c>CandidateLiveVerifier</c>).
/// </summary>
[GenerateSerializer, Alias("devices.compute.engineering-changeset")]
public sealed record ConnectedEngineeringChangeSet(
    [property: Id(0)] string CommitSha,
    [property: Id(1)] string BaseSha,
    [property: Id(2)] string BranchName,
    [property: Id(3)] string Repository,
    [property: Id(4)] string[] ChangedFiles,
    [property: Id(5)] int Additions,
    [property: Id(6)] int Deletions,
    [property: Id(7)] string UnifiedDiff);

/// <summary>
/// Terminal result posted to <c>/circle/v1/compute/engineering/{jobId}/result</c> (or in the final
/// <see cref="EngineeringStreamChunk"/> over <c>/circle/v1/compute/engineering/{jobId}/stream</c>).
/// </summary>
[GenerateSerializer, Alias("devices.compute.engineering-response")]
public sealed record ConnectedEngineeringResponse(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string RunId,
    [property: Id(2)] string? FlowId = null,
    [property: Id(3)] string? Stage = null,
    [property: Id(4)] bool PausedAtApprovalGate = false,
    [property: Id(5)] ConnectedEngineeringChangeSet? ChangeSet = null,
    [property: Id(6)] EngineerVerificationReceipt? Receipt = null,
    [property: Id(7)] string[]? DiagnosticsAndTraces = null,
    [property: Id(8)] string? SummaryText = null,
    [property: Id(9)] string? Error = null,
    [property: Id(10)] string? ExecutedByMachineId = null);

/// <summary>
/// NDJSON progress frame streamed from a Paired PC over <c>POST /circle/v1/compute/engineering/{jobId}/stream</c>
/// (120 s idle / 900 s hard cap relay stream timeout).
/// </summary>
[GenerateSerializer, Alias("devices.compute.engineering-stream-chunk")]
public sealed record EngineeringStreamChunk(
    [property: Id(0)] string? Stage = null,
    [property: Id(1)] string? Progress = null,
    [property: Id(2)] string? Stdout = null,
    [property: Id(3)] string? Stderr = null,
    [property: Id(4)] bool Done = false,
    [property: Id(5)] string? Error = null,
    [property: Id(6)] ConnectedEngineeringResponse? Result = null);

/// <summary>
/// Live runtime consent store for this Desktop PC's explicit <c>AllowConnectedEngineering</c> toggle (INT-2599 Slice 2).
/// Strictly fail-closed (<see langword="false"/> by default) and completely separate from <see cref="IClientCodeSandboxConsent"/>.
/// </summary>
public interface IClientEngineeringConsent
{
    event Action<bool>? Changed;

    bool AllowConnectedEngineering { get; }

    void SetAllowConnectedEngineering(bool allow);
}

/// <summary>
/// Transport abstraction for dispatching Plane 2 self-improving engineering runs to an eligible
/// online Paired PC advertising <c>AllowConnectedEngineering == true</c>.
/// </summary>
public interface IConnectedEngineeringTransport
{
    Task<ConnectedEngineeringResponse?> ExecuteEngineeringAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        ConnectedEngineeringRequest request,
        Func<EngineeringStreamChunk, Task>? onProgress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Executes a <see cref="ConnectedEngineeringRequest"/> locally on a Paired PC using isolated git worktrees,
/// <c>AgentDevSlotsCandidateHost</c>, and <c>CandidateLiveVerifier</c>, pausing at <c>approval-gate</c>
/// to return a SHA-locked <see cref="ConnectedEngineeringChangeSet"/> and <see cref="EngineerVerificationReceipt"/>.
/// </summary>
public interface ILocalEngineeringExecutor
{
    Task<ConnectedEngineeringResponse> ExecuteAsync(
        ConnectedEngineeringRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Streaming extension for <see cref="ILocalEngineeringExecutor"/> that emits incremental
/// <see cref="EngineeringStreamChunk"/> NDJSON progress frames over <c>POST /circle/v1/compute/engineering/{jobId}/stream</c>.
/// </summary>
public interface IStreamingLocalEngineeringExecutor : ILocalEngineeringExecutor
{
    IAsyncEnumerable<EngineeringStreamChunk> ExecuteStreamingAsync(
        ConnectedEngineeringRequest request,
        CancellationToken ct = default);
}
