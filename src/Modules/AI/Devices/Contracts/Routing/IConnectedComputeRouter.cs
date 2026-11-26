using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using DigitalBrain;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Devices;

/// <summary>
/// Consent policy governing whether an enrolled device accepts Plane 1 sovereign local compute dispatches.
/// Defaults to <see cref="ExplicitToggle"/> (platform-aware Desktop ON / Mobile OFF); <see cref="AutoAll"/>
/// remains supported for backward compatibility.
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

/// <summary>Device OS family used to derive RFC-2 default consent for Connected Client Compute (<c>ICodeSandbox</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClientDevicePlatform>))]
public enum ClientDevicePlatform
{
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    [JsonStringEnumMemberName("windows")]
    Windows = 1,

    [JsonStringEnumMemberName("macos")]
    MacOS = 2,

    [JsonStringEnumMemberName("linux")]
    Linux = 3,

    [JsonStringEnumMemberName("android")]
    Android = 4,

    [JsonStringEnumMemberName("ios")]
    IOS = 5,
}

/// <summary>
/// RFC-2 policy for Connected Client Compute (<see cref="ICodeSandbox"/>):
/// defaults <c>AllowClientCodeSandbox</c> to <see langword="true"/> on Desktop (Windows, macOS, Linux)
/// and <see langword="false"/> on Mobile (Android, iOS) or unknown platforms, and enforces consent on both
/// client capability advertisement and server dispatch eligibility.
/// </summary>
public static class ClientComputeConsentPolicy
{
    /// <summary>Settings toggle child key for the Connected Client Compute device switch.</summary>
    public const string SettingsToggleKey = "allow-client-code-sandbox";

    /// <summary>Persisted user settings variable key backing the Connected Client Compute toggle.</summary>
    public const string SettingsVariableKey = "settings.toggle." + SettingsToggleKey;

    /// <summary>Canonical capability identifier advertised over the paired P-256 relay.</summary>
    public const string CodeSandboxCapability = ICodeSandbox.CapabilityId;

    public static ClientDevicePlatform DetectCurrentPlatform()
    {
        if (OperatingSystem.IsAndroid())
        {
            return ClientDevicePlatform.Android;
        }

        if (OperatingSystem.IsIOS())
        {
            return ClientDevicePlatform.IOS;
        }

        if (OperatingSystem.IsWindows())
        {
            return ClientDevicePlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return ClientDevicePlatform.MacOS;
        }

        if (OperatingSystem.IsLinux())
        {
            return ClientDevicePlatform.Linux;
        }

        return ClassifyPlatform(RuntimeInformation.OSDescription);
    }

    public static ClientDevicePlatform ClassifyPlatform(string? osDescription)
    {
        if (string.IsNullOrWhiteSpace(osDescription))
        {
            return ClientDevicePlatform.Unknown;
        }

        // Mobile checks precede Linux/Darwin because Android descriptions contain "Linux"
        // and iOS/iPadOS descriptions can contain "Darwin".
        if (osDescription.Contains("android", StringComparison.OrdinalIgnoreCase))
        {
            return ClientDevicePlatform.Android;
        }

        if (osDescription.Contains("ios", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("iphone", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("ipad", StringComparison.OrdinalIgnoreCase))
        {
            return ClientDevicePlatform.IOS;
        }

        if (osDescription.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("win32", StringComparison.OrdinalIgnoreCase))
        {
            return ClientDevicePlatform.Windows;
        }

        if (osDescription.Contains("macos", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("mac os", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("darwin", StringComparison.OrdinalIgnoreCase)
            || osDescription.Contains("os x", StringComparison.OrdinalIgnoreCase))
        {
            return ClientDevicePlatform.MacOS;
        }

        if (osDescription.Contains("linux", StringComparison.OrdinalIgnoreCase))
        {
            return ClientDevicePlatform.Linux;
        }

        return ClientDevicePlatform.Unknown;
    }

    public static bool DefaultAllowClientCodeSandbox(ClientDevicePlatform platform)
        => platform is ClientDevicePlatform.Windows
            or ClientDevicePlatform.MacOS
            or ClientDevicePlatform.Linux;

    public static bool DefaultAllowClientCodeSandbox(string? osDescription)
        => DefaultAllowClientCodeSandbox(ClassifyPlatform(osDescription));

    public static bool DefaultAllowClientCodeSandboxForCurrentPlatform()
        => DefaultAllowClientCodeSandbox(DetectCurrentPlatform());

    public static bool HasCodeSandboxCapability(string[]? advertisedCapabilities)
        => advertisedCapabilities is { Length: > 0 }
            && advertisedCapabilities.Any(static c => string.Equals(c?.Trim(), CodeSandboxCapability, StringComparison.Ordinal));

    public static string[] CapabilitiesFor(bool advertiseCodeSandbox)
        => advertiseCodeSandbox ? [CodeSandboxCapability] : [];

    public static bool IsEligibleForCodeSandbox(
        ComputeConsentMode consentMode,
        bool allowClientCodeSandbox,
        bool sandboxReady,
        string[]? advertisedCapabilities)
    {
        if (!sandboxReady)
        {
            return false;
        }

        return consentMode switch
        {
            ComputeConsentMode.ExplicitToggle => allowClientCodeSandbox && HasCodeSandboxCapability(advertisedCapabilities),
            ComputeConsentMode.AutoAll => advertisedCapabilities is null || HasCodeSandboxCapability(advertisedCapabilities),
            _ => false,
        };
    }
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

    [JsonStringEnumMemberName("aca-dynamic-sessions")]
    AcaDynamicSessions = 3,
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
    [property: Id(8)] string? KeyThumbprint = null,
    [property: Id(9)] bool AllowClientCodeSandbox = false,
    [property: Id(10)] string[]? AdvertisedCapabilities = null);

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
    [property: Id(8)] string? KeyThumbprint = null,
    [property: Id(9)] bool AllowClientCodeSandbox = false,
    [property: Id(10)] string[]? AdvertisedCapabilities = null)
{
    /// <summary>True when this node explicitly advertises the <see cref="ICodeSandbox"/> capability.</summary>
    public bool AdvertisesCodeSandbox =>
        ClientComputeConsentPolicy.HasCodeSandboxCapability(AdvertisedCapabilities);

    /// <summary>
    /// True only when the node is online, advertises local sandbox readiness, and satisfies RFC-2 consent enforcement
    /// (<see cref="ComputeConsentMode.ExplicitToggle"/> with <see cref="AllowClientCodeSandbox"/> and
    /// <see cref="ICodeSandbox"/> advertised, or backward-compatible <see cref="ComputeConsentMode.AutoAll"/>).
    /// </summary>
    public bool IsEligibleForSovereignCompute =>
        Status == ComputeNodeStatus.Online
        && ClientComputeConsentPolicy.IsEligibleForCodeSandbox(
            ConsentMode,
            AllowClientCodeSandbox,
            SandboxReady,
            AdvertisedCapabilities);
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
/// Live runtime consent store for this device's explicit <c>AllowClientCodeSandbox</c> toggle (RFC-2).
/// Falls back to platform-aware <see cref="ClientComputeConsentPolicy"/> defaults (Desktop: ON, Mobile: OFF)
/// until explicitly toggled via Settings or <c>IDeviceLink</c>.
/// </summary>
public interface IClientCodeSandboxConsent
{
    event Action<bool>? Changed;

    bool AllowClientCodeSandbox { get; }

    bool HasExplicitOverride { get; }

    bool Resolve(bool optionsDefault);

    void Set(bool allow);
}

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
