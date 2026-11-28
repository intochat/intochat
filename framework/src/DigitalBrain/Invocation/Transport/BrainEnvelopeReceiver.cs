using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Access;

namespace DigitalBrain.Invocation;

/// <summary>
/// Receiving side of cross-cluster transport: enforces freshness (±5m skew), replay defense, and signature verification
/// against pinned sender keys, decrypts via a non-exportable <see cref="IBrainKeySigner"/>, verifies caller account binding,
/// stamps the foreign person caller through <see cref="EdgeAuthority"/>, dispatches through the local host's unchanged
/// <see cref="AccessGate"/> and neuron host, and returns a sealed reply <see cref="BrainEnvelope"/>.
/// </summary>
public sealed class BrainEnvelopeReceiver
{
    private readonly Func<BrainId, bool> _isLocalBrain;
    private readonly IBrainKeySigner _signer;
    private readonly Func<BrainId, BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> _keyLookup;
    private readonly EdgeAuthority _edge;
    private readonly Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> _invoke;
    private readonly Func<NeuronId, RuntimeInvocation, Task>? _post;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxClockSkew;
    private readonly BrainEnvelopeReplayCache _replayCache;
    private readonly Func<BrainId, BrainId, string, CancellationToken, ValueTask<bool>>? _verifyCallerAccount;
    private readonly Func<BrainId, BrainId, RemoteViaProvenance, RuntimeInvocation, CancellationToken, ValueTask<bool>>? _verifyViaOffer;
    private readonly Func<BrainEnvelope, RemoteInvocationRequest, BrainPublicKeys, bool>? _allowUnpinnedHandshake;

    /// <summary>Creates a receiver for <paramref name="localBrain"/> using an in-memory <see cref="BrainKeyPair"/>.</summary>
    public BrainEnvelopeReceiver(
        BrainId localBrain,
        BrainKeyPair localKeys,
        Func<BrainId, BrainPublicKeys?> keyLookup,
        EdgeAuthority edge,
        Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> invoke,
        Func<NeuronId, RuntimeInvocation, Task>? post = null,
        TimeProvider? clock = null,
        TimeSpan? maxClockSkew = null,
        Func<BrainId, string, CancellationToken, ValueTask<bool>>? verifyCallerAccount = null,
        BrainEnvelopeReplayCache? replayCache = null,
        Func<BrainId, BrainId, RemoteViaProvenance, RuntimeInvocation, CancellationToken, ValueTask<bool>>? verifyViaOffer = null,
        Func<BrainEnvelope, RemoteInvocationRequest, BrainPublicKeys, bool>? allowUnpinnedHandshake = null)
        : this(
            brain => brain == localBrain,
            new BrainKeyPairSigner(localKeys),
            (_, sender, _) => ValueTask.FromResult(keyLookup(sender)),
            edge,
            invoke,
            post: post,
            timeProvider: clock,
            maxClockSkew: maxClockSkew,
            scopedVerifyCallerAccount: verifyCallerAccount is not null
                ? (_, sender, account, ct) => verifyCallerAccount(sender, account, ct)
                : null,
            replayCache: replayCache,
            verifyViaOffer: verifyViaOffer,
            allowUnpinnedHandshake: allowUnpinnedHandshake)
    {
        ArgumentNullException.ThrowIfNull(keyLookup);
    }

    /// <summary>Creates a receiver backed by a non-exportable <see cref="IBrainKeySigner"/>.</summary>
    public BrainEnvelopeReceiver(
        Func<BrainId, bool> isLocalBrain,
        IBrainKeySigner signer,
        Func<BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> keyLookup,
        EdgeAuthority edge,
        Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> invoke,
        Func<NeuronId, RuntimeInvocation, Task>? post = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maxClockSkew = null,
        Func<BrainId, string, CancellationToken, ValueTask<bool>>? verifyCallerAccount = null,
        BrainEnvelopeReplayCache? replayCache = null,
        Func<BrainId, BrainId, RemoteViaProvenance, RuntimeInvocation, CancellationToken, ValueTask<bool>>? verifyViaOffer = null,
        Func<BrainEnvelope, RemoteInvocationRequest, BrainPublicKeys, bool>? allowUnpinnedHandshake = null)
        : this(
            isLocalBrain,
            signer,
            (_, sender, ct) => keyLookup(sender, ct),
            edge,
            invoke,
            post: post,
            timeProvider: timeProvider,
            maxClockSkew: maxClockSkew,
            scopedVerifyCallerAccount: verifyCallerAccount is not null
                ? (_, sender, account, ct) => verifyCallerAccount(sender, account, ct)
                : null,
            replayCache: replayCache,
            verifyViaOffer: verifyViaOffer,
            allowUnpinnedHandshake: allowUnpinnedHandshake)
    {
        ArgumentNullException.ThrowIfNull(keyLookup);
    }

    /// <summary>
    /// Creates a multi-brain-aware receiver backed by a non-exportable <see cref="IBrainKeySigner"/>, passing
    /// <c>(targetBrain, senderBrain)</c> into key lookup and caller-account verification so cold-start inbound
    /// deliveries can resolve per-target-brain friend rosters even when no ambient caller is established.
    /// </summary>
    public BrainEnvelopeReceiver(
        Func<BrainId, bool> isLocalBrain,
        IBrainKeySigner signer,
        Func<BrainId, BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> scopedKeyLookup,
        EdgeAuthority edge,
        Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> invoke,
        Func<NeuronId, RuntimeInvocation, Task>? post = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maxClockSkew = null,
        Func<BrainId, BrainId, string, CancellationToken, ValueTask<bool>>? scopedVerifyCallerAccount = null,
        BrainEnvelopeReplayCache? replayCache = null,
        Func<BrainId, BrainId, RemoteViaProvenance, RuntimeInvocation, CancellationToken, ValueTask<bool>>? verifyViaOffer = null,
        Func<BrainEnvelope, RemoteInvocationRequest, BrainPublicKeys, bool>? allowUnpinnedHandshake = null)
    {
        ArgumentNullException.ThrowIfNull(isLocalBrain);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(scopedKeyLookup);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(invoke);

        var skew = maxClockSkew ?? SealedRelayBrainTransport.DefaultMaxClockSkew;
        if (skew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxClockSkew), skew, "Maximum clock skew cannot be negative.");
        }

        _isLocalBrain = isLocalBrain;
        _signer = signer;
        _keyLookup = scopedKeyLookup;
        _edge = edge;
        _invoke = invoke;
        _post = post;
        _clock = timeProvider ?? TimeProvider.System;
        _maxClockSkew = skew;
        _verifyCallerAccount = scopedVerifyCallerAccount;
        _verifyViaOffer = verifyViaOffer;
        _allowUnpinnedHandshake = allowUnpinnedHandshake;
        var retention = skew > TimeSpan.Zero ? skew + skew : TimeSpan.FromMinutes(1);
        _replayCache = replayCache ?? new BrainEnvelopeReplayCache(retentionWindow: retention);
    }

    internal static bool IsWithinClockSkew(DateTimeOffset sentAtUtc, DateTimeOffset nowUtc, TimeSpan maxClockSkew)
    {
        var delta = nowUtc - sentAtUtc;
        return delta <= maxClockSkew && delta >= -maxClockSkew;
    }

    /// <summary>
    /// Verifies freshness, replay defense, and sender signature, decrypts the request, validates caller account binding,
    /// dispatches it locally under the authenticated foreign caller, and seals the result or error into a reply <see cref="BrainEnvelope"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Thrown when the envelope fails freshness, replay, signature, decryption, or routing checks.</exception>
    public async Task<BrainEnvelope> DeliverAsync(BrainEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_isLocalBrain(envelope.To))
        {
            throw new CryptographicException($"Envelope target '{envelope.To.Value}' is not hosted by this receiver.");
        }

        var now = _clock.GetUtcNow();
        SealedRelayBrainTransport.ValidateFreshnessAndReplayPrecheck(envelope, now, _maxClockSkew, _replayCache);

        var pinnedSenderKeys = await _keyLookup(envelope.To, envelope.From, cancellationToken).ConfigureAwait(false);
        var isUnpinnedHandshake = false;
        BrainPublicKeys senderKeys;
        if (pinnedSenderKeys is not null)
        {
            senderKeys = pinnedSenderKeys;
        }
        else if (_allowUnpinnedHandshake is not null && envelope.SenderPublicKeys is { } candidateKeys)
        {
            BrainPublicKeyValidation.ValidateNistP256(candidateKeys);
            senderKeys = candidateKeys;
            isUnpinnedHandshake = true;
        }
        else
        {
            throw new CryptographicException($"No public keys are known for sender brain '{envelope.From.Value}'.");
        }

        var plaintext = await BrainEnvelopeSealer.OpenAsync(envelope, _signer, senderKeys, cancellationToken).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<RemoteInvocationRequest>(plaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote invocation payload was null.");

        // Validate unpinned handshake eligibility BEFORE recording in the replay cache so rejected
        // unpinned spoofed envelopes can never pollute replay defense state.
        if (isUnpinnedHandshake && !_allowUnpinnedHandshake!(envelope, request, senderKeys))
        {
            throw new CryptographicException($"Unpinned sender brain '{envelope.From.Value}' is not permitted to invoke '{request.Target}.{request.Call.Method}'.");
        }

        if (!_replayCache.TryAccept(envelope.From, envelope.MessageId, now))
        {
            throw new CryptographicException($"Duplicate BrainEnvelope message '{envelope.MessageId}' from '{envelope.From.Value}' rejected as replay.");
        }

        RemoteInvocationResponse response;
        if (request.CallerBrain != envelope.From || request.Target.Brain != envelope.To)
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: $"Remote invocation routing mismatch: envelope from '{envelope.From.Value}' to '{envelope.To.Value}' carried caller brain '{request.CallerBrain.Value}' for target '{request.Target}'.");
        }
        else if (string.IsNullOrWhiteSpace(request.CallerAccount) || string.IsNullOrWhiteSpace(request.CallerPrincipal))
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: "Remote invocation caller account and principal must be non-empty.");
        }
        else if (!isUnpinnedHandshake
            && _verifyCallerAccount is not null
            && !await _verifyCallerAccount(envelope.To, envelope.From, request.CallerAccount, cancellationToken).ConfigureAwait(false))
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: $"Caller account '{request.CallerAccount}' is not authorized for sender brain '{envelope.From.Value}'.");
        }
        else if (request.ViaProvenance is { } viaProof
            && !RemoteViaProvenanceSigner.Verify(viaProof, envelope.From, request.Target, request.CallerAccount, senderKeys, now, _maxClockSkew))
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: "Remote invocation Via provenance failed cryptographic verification, binding, or freshness checks.");
        }
        else if (request.ViaProvenance is { } verifiedViaProof
            && _verifyViaOffer is not null
            && !await _verifyViaOffer(envelope.To, envelope.From, verifiedViaProof, request.Call, cancellationToken).ConfigureAwait(false))
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: "Remote invocation Via provenance was refused by offer policy.");
        }
        else
        {
            var verifiedCaller = _edge.VerifiedRemotePerson(
                request.CallerPrincipal,
                request.CallerAccount,
                envelope.From,
                via: request.ViaProvenance?.Via);
            using var _ = _edge.Enter(verifiedCaller);
            try
            {
                if ((request.OneWay || envelope.OneWay) && _post is not null)
                {
                    await _post(request.Target, request.Call).ConfigureAwait(false);
                    response = new RemoteInvocationResponse();
                }
                else
                {
                    var result = await _invoke(request.Target, request.Call, cancellationToken).ConfigureAwait(false);
                    response = new RemoteInvocationResponse(Result: result);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                response = new RemoteInvocationResponse(
                    ErrorType: error.GetType().Name,
                    ErrorMessage: error.Message);
            }
        }

        var replyPlaintext = JsonSerializer.SerializeToUtf8Bytes(response, RuntimeProtocol.Json);
        return await BrainEnvelopeSealer.SealAsync(
            envelope.To,
            envelope.From,
            Guid.NewGuid().ToString("N"),
            replyPlaintext,
            _signer,
            senderKeys,
            replyToMessageId: envelope.MessageId,
            sentAtUtc: _clock.GetUtcNow(),
            oneWay: false,
            ct: cancellationToken).ConfigureAwait(false);
    }
}
