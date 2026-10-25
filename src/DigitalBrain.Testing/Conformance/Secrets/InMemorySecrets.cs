using System.Collections.Concurrent;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Testing.Conformance.Secrets;

/// <summary>
/// The secrets@1 reference implementation: per-brain named values in memory. It passes <see cref="SecretsConformance{TSecrets}"/>.
/// References are <c>&lt;brain&gt;/&lt;name&gt;</c>; resolving another brain's reference is <see cref="SecretDenied"/>. Concurrent
/// sets of one name are last-writer-wins. <see cref="Log"/> records operations by name, never values.
/// </summary>
/// <param name="callers">Supplies the runtime caller; a local owner when null.</param>
public class InMemorySecrets(AmbientCallerScope? callers = null) : ISecrets
{
    private readonly AmbientCallerScope _callers = callers ?? new AmbientCallerScope();
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _log = new();

    /// <summary>The operation log lines (names and references only).</summary>
    public IReadOnlyList<string> Log => [.. _log];

    /// <summary>Stores <paramref name="value"/> as <paramref name="name"/> in <paramref name="brainId"/> without a caller.</summary>
    public SecretRef Put(string brainId, string name, string label, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var reference = $"{brainId}/{name}";
        _values[reference] = value;
        _log.Enqueue($"set {reference}");
        return new SecretRef { Reference = reference, Label = label ?? "", Status = SecretStatus.Set };
    }

    /// <inheritdoc />
    public Task<SecretRef> Set(string name, string label, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OnCall(nameof(Set));
        return Task.FromResult(Put(_callers.Current.BrainId, name, label, value));
    }

    /// <inheritdoc />
    public Task Remove(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        OnCall(nameof(Remove));
        var reference = $"{_callers.Current.BrainId}/{name}";
        _values.TryRemove(reference, out _);
        _log.Enqueue($"remove {reference}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> Resolve(SecretRef secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        OnCall(nameof(Resolve));
        var reference = secret.Reference;
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        var name = slash < 0 ? reference : reference[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(name)) { throw new SecretNotFound(string.IsNullOrWhiteSpace(reference) ? "(empty)" : reference); }
        if (slash < 0 || !string.Equals(reference[..slash], _callers.Current.BrainId, StringComparison.Ordinal))
        {
            _log.Enqueue($"denied {reference}");
            throw new SecretDenied(name);
        }

        if (!_values.TryGetValue(reference, out var value))
        {
            _log.Enqueue($"not-found {reference}");
            throw new SecretNotFound(name);
        }

        _log.Enqueue($"resolve {reference}");
        return Task.FromResult(value);
    }

    /// <summary>Called on entry of each <see cref="ISecrets"/> member with the member name.</summary>
    protected virtual void OnCall(string member)
    {
    }
}
