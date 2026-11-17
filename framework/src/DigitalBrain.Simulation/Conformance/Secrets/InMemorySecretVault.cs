using System.Collections.Concurrent;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Secrets;

namespace DigitalBrain.Testing.Conformance.Secrets;

/// <summary>
/// An in-memory <see cref="ISecretVaultPort"/>: per-brain named values, references <c>&lt;brain&gt;/&lt;name&gt;</c>, another
/// brain's reference is <see cref="SecretVaultStatus.Denied"/>. Lets the runtime's secrets@1 capability run its conformance kit
/// without a host vault. <see cref="Log"/> records operations by reference, never values. Seal and Open use AES-GCM under a
/// random per-instance key; named secrets are the values given to <see cref="Provide"/> (the runtime checks declarations).
/// </summary>
public sealed class InMemorySecretVault : ISecretVaultPort
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _log = new();
    private readonly ConcurrentQueue<Caller> _seen = new();
    private readonly ICallerAmbient _callers;

    /// <summary>Creates the vault.</summary>
    /// <param name="callers">The ambient the stamped caller is read from; the TestOS ambient when null.</param>
    public InMemorySecretVault(ICallerAmbient? callers = null) => _callers = callers ?? TestCallerAmbient.Instance;

    private Caller Stamped() => _callers.Current ?? throw new UnauthorizedAccessException("secrets@1: the call carries no caller.");

    private static string Who(Caller caller) => caller.For?.Principal ?? caller.Actor.GetType().Name;
    private readonly ConcurrentDictionary<string, string> _named = new(StringComparer.Ordinal);
    private readonly KitSeal _seal = new();

    /// <summary>Configures the deployment-provided secret <paramref name="name"/>.</summary>
    public void Provide(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _named[name] = value;
    }

    /// <inheritdoc />
    public Task<SealedSecret?> Seal(string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        _log.Enqueue($"seal {context} by {Who(caller)}");
        return Task.FromResult<SealedSecret?>(_seal.Seal(context, plaintext.Span));
    }

    /// <inheritdoc />
    public Task<byte[]?> Open(string context, SealedSecret sealedSecret, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        var plaintext = _seal.Open(context, sealedSecret);
        _log.Enqueue(plaintext is null ? $"denied open {context} for {Who(caller)}" : $"open {context} by {Who(caller)}");
        return Task.FromResult(plaintext);
    }

    /// <inheritdoc />
    public Task<SecretVaultResult> Named(string name, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        var reference = SecretRef.NamedPrefix + name;
        return Task.FromResult(SecretVaultResult.Stored(_named.ContainsKey(name)
            ? new SecretRef { Reference = reference, Label = name, Status = SecretStatus.Set }
            : SecretRef.Unset(reference, name)));
    }

    /// <summary>The operation log lines (references and callers only).</summary>
    public IReadOnlyList<string> Log => [.. _log];

    /// <summary>Every caller the port was handed, in call order.</summary>
    public IReadOnlyList<Caller> Callers => [.. _seen];

    /// <inheritdoc />
    public Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        var reference = $"{caller.Brain.Value}/{name}";
        _values[reference] = value;
        _log.Enqueue($"set {reference} by {Who(caller)}");
        return Task.FromResult(SecretVaultResult.Stored(new SecretRef { Reference = reference, Label = label ?? "", Status = SecretStatus.Set }));
    }

    /// <inheritdoc />
    public Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        var reference = $"{caller.Brain.Value}/{name}";
        _values.TryRemove(reference, out _);
        _log.Enqueue($"remove {reference} by {Who(caller)}");
        return Task.FromResult(SecretVaultStatus.Ok);
    }

    /// <inheritdoc />
    public Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
    {
        var caller = Stamped();
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        _seen.Enqueue(caller);
        if (secret.IsNamed)
        {
            _log.Enqueue($"resolve {secret.Reference} by {Who(caller)}");
            return Task.FromResult(_named.TryGetValue(secret.Reference[SecretRef.NamedPrefix.Length..], out var provided)
                ? SecretVaultResult.Resolved(provided)
                : SecretVaultResult.NotFound);
        }

        var reference = secret.Reference;
        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || slash == reference.Length - 1) { return Task.FromResult(SecretVaultResult.NotFound); }
        if (!string.Equals(reference[..slash], caller.Brain.Value, StringComparison.Ordinal))
        {
            _log.Enqueue($"denied {reference} for {Who(caller)}");
            return Task.FromResult(SecretVaultResult.Denied);
        }

        if (!_values.TryGetValue(reference, out var value))
        {
            _log.Enqueue($"not-found {reference}");
            return Task.FromResult(SecretVaultResult.NotFound);
        }

        _log.Enqueue($"resolve {reference} by {Who(caller)}");
        return Task.FromResult(SecretVaultResult.Resolved(value));
    }
}
