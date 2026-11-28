using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace DigitalBrain.Invocation;

/// <summary>
/// Thread-safe in-memory <see cref="IBrainKeyDirectory"/> enforcing immutable TOFU key pinning and signed rotation proofs.
/// </summary>
public sealed class InMemoryPinnedBrainKeyDirectory : IBrainKeyDirectory
{
    private readonly ConcurrentDictionary<string, BrainPublicKeys> _pins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Guid Token, BrainPublicKeys Keys)> _transient = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private sealed class TransientScope(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<BrainPublicKeys?> GetPinnedKeysAsync(BrainId brainId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = brainId.Value ?? string.Empty;
        if (_pins.TryGetValue(key, out var keys))
        {
            return ValueTask.FromResult<BrainPublicKeys?>(keys);
        }

        if (_transient.TryGetValue(key, out var transient))
        {
            return ValueTask.FromResult<BrainPublicKeys?>(transient.Keys);
        }

        return ValueTask.FromResult<BrainPublicKeys?>(null);
    }

    /// <inheritdoc />
    public ValueTask<IDisposable> BeginTransientHandshakeKeyAsync(
        BrainId localBrain,
        BrainId remoteBrain,
        BrainPublicKeys candidateKeys,
        CancellationToken ct = default)
    {
        _ = localBrain;
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidateKeys);
        BrainPublicKeyValidation.ValidateNistP256(candidateKeys);

        var key = remoteBrain.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("BrainId must be non-empty.", nameof(remoteBrain));
        }

        lock (_gate)
        {
            if (_pins.TryGetValue(key, out var existing))
            {
                if (!existing.Matches(candidateKeys))
                {
                    throw new CryptographicException(
                        $"Public keys for brain '{key}' are already pinned and cannot be replaced by a handshake bundle.");
                }

                return ValueTask.FromResult<IDisposable>(new TransientScope(static () => { }));
            }

            var token = Guid.NewGuid();
            var cloned = new BrainPublicKeys((byte[])candidateKeys.SigningKey.Clone(), (byte[])candidateKeys.ExchangeKey.Clone());
            _transient[key] = (token, cloned);
            return ValueTask.FromResult<IDisposable>(new TransientScope(() =>
            {
                lock (_gate)
                {
                    if (_transient.TryGetValue(key, out var current) && current.Token == token)
                    {
                        _transient.TryRemove(key, out _);
                    }
                }
            }));
        }
    }

    /// <inheritdoc />
    public ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(publicKeys);
        BrainPublicKeyValidation.ValidateNistP256(publicKeys);

        var key = brainId.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("BrainId must be non-empty.", nameof(brainId));
        }

        lock (_gate)
        {
            if (_pins.TryGetValue(key, out var existing))
            {
                if (!existing.Matches(publicKeys))
                {
                    throw new CryptographicException(
                        $"Public keys for brain '{key}' are already pinned and cannot be silently replaced without a signed key-rotation proof.");
                }

                return ValueTask.CompletedTask;
            }

            _pins[key] = new BrainPublicKeys((byte[])publicKeys.SigningKey.Clone(), (byte[])publicKeys.ExchangeKey.Clone());
            return ValueTask.CompletedTask;
        }
    }

    /// <inheritdoc />
    public ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proof);
        BrainPublicKeyValidation.ValidateNistP256(proof.NewKeys);

        var key = proof.BrainId.Value ?? string.Empty;
        lock (_gate)
        {
            if (!_pins.TryGetValue(key, out var current))
            {
                throw new CryptographicException($"Cannot rotate keys for brain '{key}': no keys are currently pinned.");
            }

            if (!BrainKeyRotationSigner.Verify(proof, current))
            {
                throw new CryptographicException($"Key rotation proof for brain '{key}' failed signature verification.");
            }

            _pins[key] = new BrainPublicKeys((byte[])proof.NewKeys.SigningKey.Clone(), (byte[])proof.NewKeys.ExchangeKey.Clone());
            return ValueTask.CompletedTask;
        }
    }
}
