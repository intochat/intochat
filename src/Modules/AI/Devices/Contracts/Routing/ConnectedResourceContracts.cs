using System.ComponentModel;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Locked Slice 1 Mode B PC resource request dispatched over <c>/circle/v1/compute/resource/dispatch</c>
/// and polled by the PC via <c>/circle/v1/compute/resource/next</c> (camelCase JSON).
/// </summary>
[GenerateSerializer, Alias("devices.compute.pc-resource-request")]
public sealed record PcResourceRequest(
    [property: Id(0)] string JobId,
    [property: Id(1)] string BrainId,
    [property: Id(2)] string Capability,
    [property: Id(3)] string Operation,
    [property: Id(4)] string? PayloadJson = null,
    [property: Id(5)] string? ApprovalToken = null,
    [property: Id(6)] int TimeoutSeconds = 60);

/// <summary>
/// Locked Slice 1 Mode B PC resource response posted to <c>/circle/v1/compute/resource/{jobId}/result</c>
/// and returned from <c>/circle/v1/compute/resource/dispatch</c> (camelCase JSON).
/// </summary>
[GenerateSerializer, Alias("devices.compute.pc-resource-response")]
public sealed record PcResourceResponse(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string? ResultJson = null,
    [property: Id(2)] string? Error = null,
    [property: Id(3)] string? ExecutedByMachineId = null);

/// <summary>Locked Slice 1 NDJSON chunk schema for live sandbox streaming over <c>/circle/v1/compute/sandbox/{jobId}/stream</c>.</summary>
[GenerateSerializer, Alias("devices.compute.sandbox-stream-chunk")]
public sealed record SandboxStreamChunk(
    [property: Id(0)] string? Stdout = null,
    [property: Id(1)] string? Stderr = null,
    [property: Id(2)] string? Progress = null,
    [property: Id(3)] bool Done = false,
    [property: Id(4)] string? Error = null,
    [property: Id(5)] ConnectedSandboxResponse? Result = null);

/// <summary>Structured entry returned by Mode B consented folder list/search/stat operations.</summary>
[GenerateSerializer, Alias("devices.compute.resource-entry")]
public sealed record ConnectedResourceEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] string RelativePath,
    [property: Id(2)] bool IsDirectory,
    [property: Id(3)] long SizeBytes,
    [property: Id(4)] DateTimeOffset LastModifiedUtc);

/// <summary>Payload JSON for Mode B <c>IWindowsFolderGateway</c> operations.</summary>
public sealed record FolderResourcePayload(
    string? RootLabel = null,
    string? RelativePath = null,
    string? Content = null,
    string? SearchPattern = null);

/// <summary>Payload JSON for Mode B <c>IConsole</c> operations.</summary>
public sealed record ConsoleResourcePayload(
    string Command,
    string? RootLabel = null,
    string? WorkingDirectory = null,
    string? ApprovalPlanId = null);

/// <summary>Result JSON for Mode B <c>IWindowsFolderGateway</c> operations.</summary>
public sealed record FolderResourceResult(
    string? TextContent = null,
    bool? Exists = null,
    ConnectedResourceEntry[]? Entries = null);

/// <summary>Result JSON for Mode B <c>IConsole</c> operations.</summary>
public sealed record ConsoleResourceResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>Safe metadata for a pending anti-drive-by <c>intochat://enroll</c> handoff (never exposes raw code).</summary>
public sealed record PendingDeviceEnrollmentInfo(
    Uri ServerOrigin,
    DateTimeOffset ReceivedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// In-memory store for pending <c>intochat://enroll</c> protocol handoffs on Desktop.
/// Holds the single-use code strictly in memory (never persisted or logged) until the user explicitly confirms or cancels.
/// </summary>
public interface IPendingDeviceEnrollmentStore
{
    PendingDeviceEnrollmentInfo? Current { get; }

    PendingDeviceEnrollmentInfo Stage(Uri serverOrigin, Code code);

    bool TryConsume(out Uri? serverOrigin, out Code code);

    void Clear();
}

/// <summary>Live runtime consent store for Mode B PC resource relay capabilities (default OFF).</summary>
public interface IClientResourceConsent
{
    event Action? Changed;

    bool AllowWindowsFolderGateway { get; }

    bool AllowConsole { get; }

    void SetAllowWindowsFolderGateway(bool allow);

    void SetAllowConsole(bool allow);
}

/// <summary>Transport abstraction for dispatching Mode B PC resource operations to a connected client.</summary>
public interface IConnectedResourceTransport
{
    Task<PcResourceResponse?> ExecuteResourceAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        PcResourceRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Executes a Mode B <see cref="PcResourceRequest"/> locally on a Windows PC, enforcing
/// <c>PathConfinement.Resolve</c> against user-consented roots and verifying <c>[PersonOnly]</c> approval
/// proof before invoking confined shell commands.
/// </summary>
public interface ILocalPcResourceExecutor
{
    Task<string[]> GetConsentedFolderLabelsAsync(CancellationToken ct = default);

    Task<PcResourceResponse> ExecuteAsync(
        PcResourceRequest request,
        CancellationToken ct = default);
}

/// <summary>Summary of a connected PC exposing Mode B consented folders and/or [PersonOnly] shell.</summary>
[GenerateSerializer, Alias("devices.pc-resource.node")]
public sealed record ConnectedPcResourceNode(
    [property: Id(0)] string MachineId,
    [property: Id(1)] string DeviceName,
    [property: Id(2)] bool Online,
    [property: Id(3)] bool AllowsFolders,
    [property: Id(4)] bool AllowsConsole,
    [property: Id(5)] bool AllowsCodeSandbox,
    [property: Id(6)] string[] ConsentedFolders);

/// <summary>Result of a Mode B [PersonOnly] remote shell request (either waiting for approval or executed).</summary>
[GenerateSerializer, Alias("devices.pc-resource.shell-outcome")]
public sealed record RemotePcShellOutcome(
    [property: Id(0)] string Status,
    [property: Id(1)] string? PlanId = null,
    [property: Id(2)] int? ExitCode = null,
    [property: Id(3)] string? StandardOutput = null,
    [property: Id(4)] string? StandardError = null,
    [property: Id(5)] string? Error = null,
    [property: Id(6)] string? ExecutedByMachineId = null);

/// <summary>
/// Cloud-brain proxy neuron (<c>devices.pc-resource</c>) allowing cloud agents and orchestration flows
/// to discover connected Windows PCs, inspect/edit user-consented folders over the Circle relay,
/// and request durable <c>[PersonOnly]</c> human approval before running shell commands on the PC.
/// </summary>
[NeuronAlias("devices.pc-resource"), DefaultNeuronType("devices.pc-resource")]
[Description("Discover connected Windows PCs, read/edit user-consented PC folders over the Circle relay, and request [PersonOnly] human approval to run confined shell commands on a connected PC.")]
public interface IRemotePcResource
{
    const string DefaultKey = "pc-resources";

    static NeuronId Key(string brainId) => new(new BrainId(brainId), "devices.pc-resource", DefaultKey);

    [ReadOnlyCall]
    [Description("List connected PCs for this brain with their online status, advertised capabilities, and consented folder labels.")]
    Task<ConnectedPcResourceNode[]> ListConnectedPcsAsync(CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    [Description("List files and directories inside a user-consented folder on a connected PC.")]
    Task<ConnectedResourceEntry[]> ListFolderAsync(string rootLabel, string relativePath = "", string? machineId = null, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    [Description("Read a UTF-8 text file inside a user-consented folder on a connected PC.")]
    Task<string> ReadFileTextAsync(string rootLabel, string relativePath, string? machineId = null, CancellationToken cancellationToken = default);

    [Description("Write a UTF-8 text file inside a user-consented folder on a connected PC.")]
    Task WriteFileTextAsync(string rootLabel, string relativePath, string content, string? machineId = null, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    [Description("Search for files matching a pattern inside a user-consented folder on a connected PC.")]
    Task<ConnectedResourceEntry[]> SearchFolderAsync(string rootLabel, string pattern, string relativePath = "", string? machineId = null, CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    [Description("Stat a file or directory inside a user-consented folder on a connected PC.")]
    Task<ConnectedResourceEntry?> StatPathAsync(string rootLabel, string relativePath, string? machineId = null, CancellationToken cancellationToken = default);

    [Description("Request [PersonOnly] human approval to run a confined shell command on a connected PC. Returns status='approval_required' with a planId until approved by the person, or executes once approved.")]
    Task<RemotePcShellOutcome> RunConsoleCommandAsync(
        string command,
        string? rootLabel = null,
        int timeoutSeconds = 60,
        string? machineId = null,
        string? approvedPlanId = null,
        CancellationToken cancellationToken = default);
}
