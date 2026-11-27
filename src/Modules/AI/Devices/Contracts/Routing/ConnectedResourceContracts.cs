using System.ComponentModel;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Devices;

/// <summary>Mode B PC resource operation dispatched over the authenticated Circle relay.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectedResourceKind>))]
public enum ConnectedResourceKind
{
    [JsonStringEnumMemberName("folder-list")]
    FolderList = 0,

    [JsonStringEnumMemberName("folder-read-text")]
    FolderReadText = 1,

    [JsonStringEnumMemberName("folder-write-text")]
    FolderWriteText = 2,

    [JsonStringEnumMemberName("folder-exists")]
    FolderExists = 3,

    [JsonStringEnumMemberName("folder-search")]
    FolderSearch = 4,

    [JsonStringEnumMemberName("folder-stat")]
    FolderStat = 5,

    [JsonStringEnumMemberName("console-run")]
    ConsoleRun = 6,
}

/// <summary>Structured entry returned by Mode B consented folder list/search/stat operations.</summary>
[GenerateSerializer, Alias("devices.compute.resource-entry")]
public sealed record ConnectedResourceEntry(
    [property: Id(0)] string Name,
    [property: Id(1)] string RelativePath,
    [property: Id(2)] bool IsDirectory,
    [property: Id(3)] long SizeBytes,
    [property: Id(4)] DateTimeOffset LastModifiedUtc);

/// <summary>
/// Mode B PC resource request dispatched from a Cloud brain to a paired Windows PC over
/// <c>/circle/v1/compute/resource/dispatch</c> and polled via <c>/circle/v1/compute/resource/next</c>.
/// </summary>
[GenerateSerializer, Alias("devices.compute.resource-request")]
public sealed record ConnectedResourceRequest(
    [property: Id(0)] string JobId,
    [property: Id(1)] string BrainId,
    [property: Id(2)] ConnectedResourceKind Kind,
    [property: Id(3)] string? RootLabel = null,
    [property: Id(4)] string? RelativePath = null,
    [property: Id(5)] string? Content = null,
    [property: Id(6)] string? SearchPattern = null,
    [property: Id(7)] string? Command = null,
    [property: Id(8)] string? WorkingDirectory = null,
    [property: Id(9)] int TimeoutSeconds = 60,
    [property: Id(10)] string? ApprovalPlanId = null,
    [property: Id(11)] string? ApprovalProof = null);

/// <summary>Structured result returned by a paired Windows PC for a Mode B resource request.</summary>
[GenerateSerializer, Alias("devices.compute.resource-response")]
public sealed record ConnectedResourceResponse(
    [property: Id(0)] bool Handled,
    [property: Id(1)] string? Error = null,
    [property: Id(2)] string? TextContent = null,
    [property: Id(3)] bool? Exists = null,
    [property: Id(4)] ConnectedResourceEntry[]? Entries = null,
    [property: Id(5)] int? ExitCode = null,
    [property: Id(6)] string? StandardOutput = null,
    [property: Id(7)] string? StandardError = null,
    [property: Id(8)] string? ExecutedByMachineId = null);

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
    Task<ConnectedResourceResponse?> ExecuteResourceAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        ConnectedResourceRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Executes a Mode B <see cref="ConnectedResourceRequest"/> locally on a Windows PC, enforcing
/// <c>PathConfinement.Resolve</c> against user-consented roots and verifying <c>[PersonOnly]</c> approval
/// proof before invoking confined shell commands.
/// </summary>
public interface ILocalPcResourceExecutor
{
    Task<string[]> GetConsentedFolderLabelsAsync(CancellationToken ct = default);

    Task<ConnectedResourceResponse> ExecuteAsync(
        ConnectedResourceRequest request,
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
