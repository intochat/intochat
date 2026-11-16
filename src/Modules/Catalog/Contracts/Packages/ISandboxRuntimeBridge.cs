namespace IntoChat.OS.Modules.Apps;

/// <summary>Response from deploying a compiled <see cref="AppPackage"/> into the sandbox runtime.</summary>
public sealed record SandboxDeploymentResult(
    bool Succeeded,
    string? Error,
    string[] DiscoveredSignals,
    string[] DiscoveredSchedules,
    string[] DiscoveredCommands,
    DigitalBrain.UI.View? InitialView);

/// <summary>Response from dispatching a signal, schedule tick, or UI/command invocation in the sandbox.</summary>
public sealed record SandboxDispatchResult(
    bool Handled,
    string? ResultJson,
    string? UpdatedStateJson,
    DigitalBrain.UI.View? UpdatedView,
    string? Error);

/// <summary>Bridge between OS Catalog/Apps and the hardened container worker (<c>IntoChat.OS.Sandbox</c>).</summary>
public interface ISandboxRuntimeBridge
{
    Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        CancellationToken cancellationToken = default);

    Task<SandboxDispatchResult> DispatchSignalAsync(
        string brainId,
        string appKey,
        string signalName,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task<SandboxDispatchResult> DispatchScheduleAsync(
        string brainId,
        string appKey,
        string cron,
        CancellationToken cancellationToken = default);

    Task<SandboxDispatchResult> DispatchCommandAsync(
        string brainId,
        string appKey,
        string command,
        string? payloadJson = null,
        CancellationToken cancellationToken = default);

    Task<DigitalBrain.UI.View?> RenderAsync(
        string brainId,
        string appKey,
        CancellationToken cancellationToken = default);

    Task ForwardSignalAsync(
        string brainId,
        string signalName,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task UnloadAsync(
        string brainId,
        string appKey,
        CancellationToken cancellationToken = default);
}
