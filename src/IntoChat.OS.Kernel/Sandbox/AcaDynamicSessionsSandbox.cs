using DigitalBrain;
using DigitalBrain.Log;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Plane 1 headless cloud fallback <see cref="ICodeSandbox"/> executing AST-sanitized, compiled C#
/// orchestration packages inside isolated Azure Container Apps (ACA) Dynamic Sessions Kata MicroVMs.
/// Remains dormant unless <see cref="AcaDynamicSessionsOptions.PoolManagementEndpoint"/> is configured,
/// and fails closed if invoked while unconfigured.
/// </summary>
internal sealed class AcaDynamicSessionsSandbox : ICodeSandbox
{
    public const string ExecutionTargetName = "aca-dynamic-sessions";

    private readonly AcaDynamicSessionsClient _client;
    private readonly IOptions<SandboxOptions> _sandboxOptions;
    private readonly AppTokenService _tokens;
    private readonly ILogger<AcaDynamicSessionsSandbox> _logger;

    public AcaDynamicSessionsSandbox(
        IHttpClientFactory httpClientFactory,
        IOptions<SandboxOptions> sandboxOptions,
        AppTokenService tokens,
        IAcaDynamicSessionsTokenProvider tokenProvider,
        ILogger<AcaDynamicSessionsSandbox> logger,
        ILogger<AcaDynamicSessionsClient>? clientLogger = null)
        : this(
            new AcaDynamicSessionsClient(
                httpClientFactory,
                Options.Create((sandboxOptions ?? throw new ArgumentNullException(nameof(sandboxOptions))).Value.AcaDynamicSessions),
                tokenProvider,
                clientLogger),
            sandboxOptions,
            tokens,
            logger)
    {
    }

    internal AcaDynamicSessionsSandbox(
        AcaDynamicSessionsClient client,
        IOptions<SandboxOptions> sandboxOptions,
        AppTokenService tokens,
        ILogger<AcaDynamicSessionsSandbox> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sandboxOptions = sandboxOptions ?? throw new ArgumentNullException(nameof(sandboxOptions));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True when an ACA Dynamic Sessions pool management endpoint is configured.</summary>
    public bool IsConfigured => _client.IsConfigured;

    /// <summary>Most recent Kata MicroVM session identifier allocated by this sandbox instance.</summary>
    public string? LastSessionId { get; private set; }

    public async Task<SandboxDispatchResult> ExecuteScopedCommandAsync(
        BrainId brainId,
        AppPackage package,
        byte[] assemblyBytes,
        string command,
        string? payloadJson,
        IReadOnlyList<string> allowedContracts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(assemblyBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(allowedContracts);
        cancellationToken.ThrowIfCancellationRequested();

        // Fail closed immediately when ACA Dynamic Sessions is not configured.
        _client.EnsureConfigured();

        var normalizedScope = NeuronContractDiscovery.NormalizeScopeList(allowedContracts);
        var scopedToken = _tokens.IssueScoped(brainId, package.Key, normalizedScope);
        var sessionId = AcaSessionIsolation.BuildPlane1SessionId(brainId.Value, package.Key);
        LastSessionId = sessionId;

        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(_client.CurrentOptions.ExecutionTimeout.TotalSeconds));
        var request = new AcaExecutionRequest(
            Mode: "plane1-code-sandbox",
            Action: "execute-scoped-command",
            TimeoutInSeconds: timeoutSeconds,
            BrainId: brainId.Value,
            AppKey: package.Key,
            Version: package.Version,
            Digest: package.Digest,
            AssemblyBase64: Convert.ToBase64String(assemblyBytes),
            Command: command,
            PayloadJson: payloadJson,
            AllowedContracts: normalizedScope,
            KernelCallbackUrl: ResolveExternalCallbackUrl(_sandboxOptions.Value.KernelCallbackUrl),
            AppToken: scopedToken,
            Environment: null);

        try
        {
            var response = await _client.ExecuteAsync(sessionId, request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Executed scoped Plane 1 command '{Command}' for '{BrainId}/{AppKey}' in ACA Dynamic Sessions Kata MicroVM '{SessionId}' (status={Status}).",
                command,
                brainId.Value,
                package.Key,
                sessionId,
                response.Status);

            return new SandboxDispatchResult(
                Handled: response.Handled,
                ResultJson: response.ResultJson,
                UpdatedStateJson: response.UpdatedStateJson,
                UpdatedView: null,
                Error: response.Error,
                ExecutionTarget: ExecutionTargetName,
                ExecutedByMachineId: null);
        }
        finally
        {
            try
            {
                await _client.TerminateSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _logger.LogSwallowed(error, $"Best-effort ACA Dynamic Sessions teardown failed for Plane 1 session '{sessionId}'.");
            }
        }
    }

    internal static string? ResolveExternalCallbackUrl(string? configuredCallbackUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredCallbackUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(configuredCallbackUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Never leak pipe:// or host loopback endpoints to remote cloud Kata MicroVMs.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (uri.IsLoopback)
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }
}
