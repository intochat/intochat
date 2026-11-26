using System.Text.Json.Serialization;
using DigitalBrain;

namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Consent policy governing whether an enrolled device automatically accepts Plane 1 sovereign local compute dispatches.
/// Defaults to <see cref="AutoAll"/> per owner architectural direction.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComputeConsentMode>))]
public enum ComputeConsentMode
{
    [JsonStringEnumMemberName("auto-all")]
    AutoAll = 0,

    [JsonStringEnumMemberName("explicit-toggle")]
    ExplicitToggle = 1,

    [JsonStringEnumMemberName("disabled")]
    Disabled = 2,
}

/// <summary>Live reachability state of an enrolled compute node based on its compute heartbeat.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComputeNodeStatus>))]
public enum ComputeNodeStatus
{
    [JsonStringEnumMemberName("offline")]
    Offline = 0,

    [JsonStringEnumMemberName("online")]
    Online = 1,
}

/// <summary>Target runtime selected for Plane 1 code-orchestration sandbox execution.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ComputeExecutionTarget>))]
public enum ComputeExecutionTarget
{
    [JsonStringEnumMemberName("connected-client")]
    ConnectedClient = 0,

    [JsonStringEnumMemberName("cloud-sandbox")]
    CloudSandbox = 1,

    [JsonStringEnumMemberName("local-unit-host")]
    LocalUnitHost = 2,
}

/// <summary>
/// Capability and liveness heartbeat advertised by an enrolled owner device over its authenticated P-256 channel.
/// </summary>
[GenerateSerializer, Alias("devices.compute.capability")]
public sealed record ComputeCapabilityAdvertisement(
    [property: Id(0)] string MachineId,
    [property: Id(1)] string DeviceName,
    [property: Id(2)] ComputeConsentMode ConsentMode,
    [property: Id(3)] bool SandboxReady,
    [property: Id(4)] string SandboxRuntime,
    [property: Id(5)] string OsDescription,
    [property: Id(6)] int MaxConcurrency,
    [property: Id(7)] DateTimeOffset HeartbeatAt,
    [property: Id(8)] string? KeyThumbprint = null);

/// <summary>Observed online/offline status and compute capability of an enrolled device belonging to a brain.</summary>
[GenerateSerializer, Alias("devices.compute.node")]
public sealed record ConnectedComputeNode(
    [property: Id(0)] string BrainId,
    [property: Id(1)] string MachineId,
    [property: Id(2)] string DeviceName,
    [property: Id(3)] ComputeConsentMode ConsentMode,
    [property: Id(4)] bool SandboxReady,
    [property: Id(5)] string SandboxRuntime,
    [property: Id(6)] ComputeNodeStatus Status,
    [property: Id(7)] DateTimeOffset LastHeartbeatAt,
    [property: Id(8)] string? KeyThumbprint = null)
{
    /// <summary>
    /// True only when the node is online, advertises local sandbox readiness, and uses <see cref="ComputeConsentMode.AutoAll"/>.
    /// </summary>
    public bool IsEligibleForSovereignCompute =>
        Status == ComputeNodeStatus.Online
        && SandboxReady
        && ConsentMode == ComputeConsentMode.AutoAll;
}

/// <summary>Routing decision produced by <see cref="IConnectedComputeRouter"/> for a brain's sandbox dispatch.</summary>
public sealed record ComputeRouteDecision(
    ComputeExecutionTarget Target,
    ConnectedComputeNode? Node,
    string Reason);

/// <summary>
/// Scoped, single-shot sandbox execution payload dispatched to a connected client's local
/// <c>IntoChat.OS.Sandbox --unit-host</c>. Contains only a short-lived least-privilege <c>AppToken</c>
/// and edge callback URL (<c>/edge/sandbox/invoke</c>) — never direct Orleans cluster credentials.
/// </summary>
[GenerateSerializer, Alias("devices.compute.sandbox-request")]
public sealed record ConnectedSandboxRequest(
    [property: Id(0)] string JobId,
    [property: Id(1)] string BrainId,
    [property: Id(2)] string AppKey,
    [property: Id(3)] string Version,
    [property: Id(4)] string Digest,
    [property: Id(5)] string AssemblyBase64,
    [property: Id(6)] string Command,
    [property: Id(7)] string? PayloadJson,
    [property: Id(8)] string? StateJson,
    [property: Id(9)] string? SettingsJson,
    [property: Id(10)] string? KernelCallbackUrl,
    [property: Id(11)] string AppToken,
    [property: Id(12)] string[] AllowedContracts);

/// <summary>Result returned by a connected client's local <c>IntoChat.OS.Sandbox --unit-host</c>.</summary>
[GenerateSerializer, Alias("devices.compute.sandbox-response")]
public sealed record ConnectedSandboxResponse(
    [property: Id(0)] bool Handled,
    [property: Id(1)] string? ResultJson,
    [property: Id(2)] string? UpdatedStateJson,
    [property: Id(3)] string? Error,
    [property: Id(4)] string? ExecutedByMachineId = null);

/// <summary>
/// Transport abstraction for probing reachability and executing a scoped sandbox job on a connected client node.
/// </summary>
public interface IConnectedComputeTransport
{
    /// <summary>Fast liveness check (&lt;500 ms budget) verifying the connected client is still reachable.</summary>
    Task<bool> ProbeAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        CancellationToken ct = default);

    /// <summary>Dispatches a scoped sandbox execution request to the connected client's local UnitHost.</summary>
    Task<ConnectedSandboxResponse?> ExecuteAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        ConnectedSandboxRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Executes a <see cref="ConnectedSandboxRequest"/> locally inside this device's out-of-process
/// <c>IntoChat.OS.Sandbox --unit-host</c>, enforcing scoped <c>AppToken</c> callbacks over <c>/edge/sandbox/invoke</c>.
/// </summary>
public interface ILocalSandboxExecutor
{
    Task<ConnectedSandboxResponse> ExecuteAsync(
        ConnectedSandboxRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Tracks connected client compute capability advertisements/heartbeats and routes Plane 1 sandbox execution
/// to an owner's online <see cref="ComputeConsentMode.AutoAll"/> local client with &lt;500 ms unavailability detection
/// and transparent fallback to Cloud Sandbox or local in-process UnitHost.
/// </summary>
public interface IConnectedComputeRouter
{
    /// <summary>Default disconnect/unavailability detection budget (strictly under 500 ms).</summary>
    static readonly TimeSpan DefaultAvailabilityTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>Default heartbeat staleness window before a silent device transitions to offline.</summary>
    static readonly TimeSpan DefaultHeartbeatTtl = TimeSpan.FromSeconds(45);

    /// <summary>Records or refreshes a compute capability advertisement and heartbeat for an enrolled device.</summary>
    Task RecordHeartbeatAsync(
        BrainId brainId,
        ComputeCapabilityAdvertisement advertisement,
        CancellationToken ct = default);

    /// <summary>Marks a device offline immediately (e.g. on disconnect, leave, timeout, or revocation).</summary>
    Task MarkOfflineAsync(
        BrainId brainId,
        string machineId,
        CancellationToken ct = default);

    /// <summary>Lists all tracked compute nodes for <paramref name="brainId"/> with current online/offline status.</summary>
    Task<ConnectedComputeNode[]> ListNodesAsync(
        BrainId brainId,
        CancellationToken ct = default);

    /// <summary>Resolves whether execution for <paramref name="brainId"/> should target a connected client or fall back.</summary>
    Task<ComputeRouteDecision> ResolveRouteAsync(
        BrainId brainId,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to execute a scoped sandbox script on an online <see cref="ComputeConsentMode.AutoAll"/> connected client.
    /// Returns <see langword="null"/> if no eligible device is online or if the client disconnects/times out
    /// within the &lt;500 ms availability budget, signaling transparent fallback to Cloud Sandbox or local UnitHost.
    /// </summary>
    Task<ConnectedSandboxResponse?> TryExecuteOnConnectedClientAsync(
        BrainId brainId,
        ConnectedSandboxRequest request,
        CancellationToken ct = default);
}
