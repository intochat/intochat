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
    string? Error,
    string? ExecutionTarget = null,
    string? ExecutedByMachineId = null);

/// <summary>Bridge between OS Catalog/Apps and the hardened container worker (<c>IntoChat.OS.Sandbox</c>).</summary>
public interface ISandboxRuntimeBridge : ICodeSandbox
{
    Task<SandboxDeploymentResult> DeployPackageAsync(
        string brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys a compiled <see cref="AppPackage"/> into the sandbox with a least-privilege scoped <c>AppToken</c>
    /// restricted strictly to <paramref name="allowedContracts"/>.
    /// </summary>
    Task<SandboxDeploymentResult> DeployScopedPackageAsync(
        DigitalBrain.BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string? stateJson,
        string? settingsJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default)
        => DeployPackageAsync(brainId.Value, package, assemblyBytes, stateJson, settingsJson, cancellationToken);

    Task<SandboxDispatchResult> ICodeSandbox.ExecuteScopedCommandAsync(
        DigitalBrain.BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string command,
        string? payloadJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken)
        => ExecuteScopedCommandAsync(brainId, package, assemblyBytes, command, payloadJson, allowedContracts, cancellationToken);

    /// <summary>
    /// Executes a single-shot scoped command inside the sandbox runtime, preferring an owner's connected
    /// consented <see cref="ICodeSandbox"/> client when online and transparently falling back to the cloud
    /// sandbox Container App or local out-of-process UnitHost when unavailable.
    /// </summary>
    new async Task<SandboxDispatchResult> ExecuteScopedCommandAsync(
        DigitalBrain.BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string command,
        string? payloadJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await DeployScopedPackageAsync(
            brainId,
            package,
            assemblyBytes,
            stateJson: null,
            settingsJson: null,
            allowedContracts: allowedContracts,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            return await DispatchCommandAsync(
                brainId.Value,
                package.Key,
                command,
                payloadJson,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await UnloadAsync(brainId.Value, package.Key, CancellationToken.None).ConfigureAwait(false);
        }
    }

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
