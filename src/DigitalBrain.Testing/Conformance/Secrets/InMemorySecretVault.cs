using System.Collections.Concurrent;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Testing.Conformance.Secrets;

/// <summary>
/// An in-memory <see cref="ISecretVaultPort"/>: per-brain named values, references <c>&lt;brain&gt;/&lt;name&gt;</c>, another
/// brain's reference is <see cref="SecretVaultStatus.Denied"/>. Lets the runtime's secrets@1 capability run its conformance kit
/// without a host vault. <see cref="Log"/> records operations by reference, never values.
/// </summary>
public sealed class InMemorySecretVault : ISecretVaultPort
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentQueue<CapabilityCaller> _callers = new();

    /// <summary>The operation log lines (references and callers only).</summary>
    public IReadOnlyList<string> Log => [.. _log];

    /// <summary>Every caller the port was handed, in call order.</summary>
    public IReadOnlyList<CapabilityCaller> Callers => [.. _callers];

    /// <inheritdoc />
    public Task<SecretVaultResult> Set(CapabilityCaller caller, string name, string label, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        _callers.Enqueue(caller);
        var reference = $"{caller.BrainId}/{name}";
        _values[reference] = value;
        _log.Enqueue($"set {reference} by {caller.PrincipalId}");
        return Task.FromResult(SecretVaultResult.Stored(new SecretRef { Reference = reference, Label = label ?? "", Status = SecretStatus.Set }));
    }

    /// <inheritdoc />
    public Task<SecretVaultStatus> Remove(CapabilityCaller caller, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        _callers.Enqueue(caller);
        var reference = $"{caller.BrainId}/{name}";
        _values.TryRemove(reference, out _);
        _log.Enqueue($"remove {reference} by {caller.PrincipalId}");
        return Task.FromResult(SecretVaultStatus.Ok);
    }

    /// <inheritdoc />
    public Task<SecretVaultResult> Resolve(CapabilityCaller caller, SecretRef secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        _callers.Enqueue(caller);
        var reference = secret.Reference;
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash == reference.Length - 1) { return Task.FromResult(SecretVaultResult.NotFound); }
        if (!string.Equals(reference[..slash], caller.BrainId, StringComparison.Ordinal))
        {
            _log.Enqueue($"denied {reference} for {caller.PrincipalId}");
            return Task.FromResult(SecretVaultResult.Denied);
        }

        if (!_values.TryGetValue(reference, out var value))
        {
            _log.Enqueue($"not-found {reference}");
            return Task.FromResult(SecretVaultResult.NotFound);
        }

        _log.Enqueue($"resolve {reference} by {caller.PrincipalId}");
        return Task.FromResult(SecretVaultResult.Resolved(value));
    }
}
