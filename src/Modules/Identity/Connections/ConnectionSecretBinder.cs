using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Modules;
using IntoChat.OS.Modules.Identity.Contracts.Connections;

namespace IntoChat.OS.Modules.Identity.Connections;

// Opens a secret reference through ISecretVaultPort (fail-closed, zero plaintext logging),
// binds it to the target connection neuron, and optionally probes the connection.
internal sealed class ConnectionSecretBinder(
    AuthorityNeurons neurons,
    ISecretVaultPort? vault = null,
    IBrainCapabilityPolicy? capabilityPolicy = null,
    ICallerAmbient? callers = null) : IConnectionSecretBinder
{
    public Task<ConnectionView> BindAsync(
        BrainId brain,
        string connectorId,
        string connectionKey,
        string authMethodId,
        string secretRef,
        Func<string, string>? formatValue = null,
        bool probe = true,
        CancellationToken ct = default)
        => BindCoreAsync(
            brain,
            connectorId,
            connectionKey,
            authMethodId,
            secretRef,
            useGrant: false,
            target: null,
            scopes: null,
            formatValue,
            probe,
            ct);

    public Task<ConnectionView> BindGrantAsync(
        BrainId brain,
        string connectorId,
        string connectionKey,
        string authMethodId,
        string secretRef,
        string? target,
        string? scopes = null,
        Func<string, string>? formatValue = null,
        bool probe = true,
        CancellationToken ct = default)
        => BindCoreAsync(
            brain,
            connectorId,
            connectionKey,
            authMethodId,
            secretRef,
            useGrant: true,
            target,
            scopes,
            formatValue,
            probe,
            ct);

    private async Task<ConnectionView> BindCoreAsync(
        BrainId brain,
        string connectorId,
        string connectionKey,
        string authMethodId,
        string secretRef,
        bool useGrant,
        string? target,
        string? scopes,
        Func<string, string>? formatValue,
        bool probe,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(authMethodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        if (capabilityPolicy is not null)
        {
            var decision = await capabilityPolicy.EvaluateAsync(brain.Value, BrainCapability.ExternalConnector, connectorId, ct).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                throw new ClaimRequiredException(decision);
            }
        }

        if (vault is null)
        {
            throw new InvalidOperationException("Secret vault is unavailable.");
        }

        var fullKey = ResolveConnectionKey(brain.Value, connectorId, connectionKey);
        var reference = NormalizeSecretReference(secretRef);
        var opened = await vault.Resolve(
            new SecretRef { Reference = reference, Label = connectorId, Status = SecretStatus.Set },
            ct).ConfigureAwait(false);

        if (opened.Status != SecretVaultStatus.Ok || string.IsNullOrWhiteSpace(opened.Value))
        {
            throw new InvalidOperationException($"Secret reference could not be opened ({opened.Status}).");
        }

        var formatted = formatValue is not null ? formatValue(opened.Value) : opened.Value;
        if (string.IsNullOrWhiteSpace(formatted))
        {
            throw new InvalidOperationException("Formatted credential value cannot be empty.");
        }

        ConnectionView view;
        if (useGrant)
        {
            var grant = new OAuthGrant(
                authMethodId,
                formatted,
                RefreshToken: null,
                ExpiresAt: null,
                Scopes: scopes ?? string.Empty,
                Target: target,
                Response: "{}");
            view = await neurons.ConnectionInbound(fullKey).AcceptGrant(grant).ConfigureAwait(false);
        }
        else
        {
            view = await neurons.Connection(fullKey).Connect(authMethodId, formatted).ConfigureAwait(false);
        }

        if (probe)
        {
            view = await neurons.Connection(fullKey).Probe().ConfigureAwait(false);
        }

        return view;
    }

    private string NormalizeSecretReference(string secretRef)
    {
        var trimmed = secretRef.Trim();
        if (trimmed.StartsWith("secret://", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var owner = callers?.Current?.For?.Principal;
        return !string.IsNullOrWhiteSpace(owner)
            ? $"secret://{owner}/{trimmed}"
            : trimmed;
    }

    private static string ResolveConnectionKey(string brainId, string connectorId, string connectionKey)
    {
        if (ConnectionKeys.TryParse(connectionKey, out var parsed))
        {
            if (!string.Equals(parsed.BrainId, brainId, StringComparison.Ordinal)
                || !string.Equals(parsed.ConnectorId, connectorId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Connection key '{connectionKey}' does not match brain '{brainId}' and connector '{connectorId}'.", nameof(connectionKey));
            }

            return ConnectionKeys.For(parsed.BrainId, parsed.ConnectorId, parsed.ConnectionId);
        }

        return ConnectionKeys.For(brainId, connectorId, connectionKey);
    }
}
