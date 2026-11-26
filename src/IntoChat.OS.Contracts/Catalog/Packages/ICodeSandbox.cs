namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Capability contract for sovereign code-orchestration sandbox execution (<c>ICodeSandbox</c>),
/// implemented by server/cloud/local <see cref="ISandboxRuntimeBridge"/> runtimes and advertised
/// by consented paired client devices over their authenticated P-256 relay channel.
/// </summary>
public interface ICodeSandbox
{
    /// <summary>Canonical capability identifier advertised by enrolled devices over the P-256 relay.</summary>
    const string CapabilityId = "ICodeSandbox";

    /// <summary>
    /// Executes a single-shot AST-sanitized, compiled orchestration script with a least-privilege scoped
    /// <c>AppToken</c> restricted strictly to <paramref name="allowedContracts"/>.
    /// </summary>
    Task<SandboxDispatchResult> ExecuteScopedCommandAsync(
        DigitalBrain.BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string command,
        string? payloadJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default);
}
