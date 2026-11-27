using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Access;

namespace DigitalBrain.Invocation;

/// <summary>
/// Cross-cluster transport for invoking neurons hosted in remote brains.
/// Caller identity is read from <see cref="ICallerAmbient"/> rather than passed as a parameter.
/// </summary>
public interface IBrainTransport
{
    /// <summary>True when <paramref name="brain"/> is hosted outside the local cluster and must be reached over this transport.</summary>
    bool IsRemote(BrainId brain);

    /// <summary>Delivers a request-response invocation to a neuron in a remote brain and returns its JSON result.</summary>
    Task<JsonElement?> InvokeRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default);

    /// <summary>Delivers a one-way invocation to a neuron in a remote brain.</summary>
    Task PostRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cryptographically signed provenance attesting that <see cref="Via"/> in <see cref="SenderBrain"/>
/// initiated a cross-brain relay call to <see cref="Target"/> on behalf of <see cref="CallerAccount"/>.
/// Carried strictly inside the E2EE <see cref="RemoteInvocationRequest"/> ciphertext (zero plaintext relay headers).
/// </summary>
public sealed record RemoteViaProvenance(
    NeuronId Via,
    BrainId SenderBrain,
    NeuronId Target,
    string CallerAccount,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    byte[] Signature);

/// <summary>
/// Signs and verifies <see cref="RemoteViaProvenance"/> attestations with NIST P-256 ECDSA over a length-prefixed
/// domain-separated canonical layout bound to sender brain, Via neuron, target neuron, caller account, and expiry.
/// </summary>
public static class RemoteViaProvenanceSigner
{
    /// <summary>Maximum permitted lifetime for a signed <see cref="RemoteViaProvenance"/> attestation (10 minutes).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);

    private static ReadOnlySpan<byte> DomainSeparator => "DigitalBrain.RemoteViaProvenance.v1"u8;

    /// <summary>Signs a <see cref="RemoteViaProvenance"/> using an in-memory <see cref="BrainKeyPair"/>.</summary>
    public static RemoteViaProvenance Sign(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        BrainKeyPair keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ValidateSignArgs(via, senderBrain, callerAccount, issuedAtUtc, expiresAtUtc);
        var canonical = BuildCanonicalBytes(via, senderBrain, target, callerAccount, issuedAtUtc, expiresAtUtc);
        var signature = keys.SigningKey.SignData(canonical, HashAlgorithmName.SHA256);
        return new RemoteViaProvenance(via, senderBrain, target, callerAccount, issuedAtUtc.ToUniversalTime(), expiresAtUtc.ToUniversalTime(), signature);
    }

    /// <summary>Signs a <see cref="RemoteViaProvenance"/> inside the non-exportable <see cref="IBrainKeySigner"/> boundary.</summary>
    public static async ValueTask<RemoteViaProvenance> SignAsync(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        IBrainKeySigner signer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ValidateSignArgs(via, senderBrain, callerAccount, issuedAtUtc, expiresAtUtc);
        var canonical = BuildCanonicalBytes(via, senderBrain, target, callerAccount, issuedAtUtc, expiresAtUtc);
        var signature = await signer.SignDataAsync(senderBrain, canonical, ct).ConfigureAwait(false);
        return new RemoteViaProvenance(via, senderBrain, target, callerAccount, issuedAtUtc.ToUniversalTime(), expiresAtUtc.ToUniversalTime(), signature);
    }

    /// <summary>
    /// Verifies that <paramref name="provenance"/> is cryptographically valid under <paramref name="expectedSenderPublicKeys"/>,
    /// bound to <paramref name="expectedSenderBrain"/>, <paramref name="expectedTarget"/>, and <paramref name="expectedCallerAccount"/>,
    /// has <see cref="RemoteViaProvenance.Via"/> hosted in <paramref name="expectedSenderBrain"/>, and is within its freshness window.
    /// </summary>
    public static bool Verify(
        RemoteViaProvenance? provenance,
        BrainId expectedSenderBrain,
        NeuronId expectedTarget,
        string expectedCallerAccount,
        BrainPublicKeys expectedSenderPublicKeys,
        DateTimeOffset nowUtc,
        TimeSpan maxClockSkew)
    {
        if (provenance is null
            || expectedSenderPublicKeys is null
            || string.IsNullOrWhiteSpace(expectedSenderBrain.Value)
            || string.IsNullOrWhiteSpace(expectedCallerAccount)
            || provenance.SenderBrain != expectedSenderBrain
            || provenance.Via.Brain != expectedSenderBrain
            || string.IsNullOrWhiteSpace(provenance.Via.Type)
            || string.IsNullOrWhiteSpace(provenance.Via.Key)
            || provenance.Target != expectedTarget
            || !string.Equals(provenance.CallerAccount, expectedCallerAccount, StringComparison.Ordinal)
            || provenance.Signature is not { Length: > 0 })
        {
            return false;
        }

        var issued = provenance.IssuedAtUtc.ToUniversalTime();
        var expires = provenance.ExpiresAtUtc.ToUniversalTime();
        var now = nowUtc.ToUniversalTime();
        var lifetime = expires - issued;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            return false;
        }

        if (expires <= now || issued > now + maxClockSkew)
        {
            return false;
        }

        try
        {
            BrainPublicKeyValidation.ValidateNistP256SigningKey(expectedSenderPublicKeys.SigningKey);
            var canonical = BuildCanonicalBytes(provenance.Via, provenance.SenderBrain, provenance.Target, provenance.CallerAccount, issued, expires);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(expectedSenderPublicKeys.SigningKey, out _);
            return verifier.VerifyData(canonical, provenance.Signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ValidateSignArgs(
        NeuronId via,
        BrainId senderBrain,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (via.Brain != senderBrain)
        {
            throw new UnauthorizedAccessException($"Cannot attest Via neuron '{via}' outside sender brain '{senderBrain.Value}'.");
        }

        if (string.IsNullOrWhiteSpace(via.Type) || string.IsNullOrWhiteSpace(via.Key))
        {
            throw new ArgumentException("Via neuron must have a non-empty type and key.", nameof(via));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(callerAccount);

        var lifetime = expiresAtUtc - issuedAtUtc;
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), $"Via provenance lifetime must be between 1 tick and {MaxLifetime}.");
        }
    }

    private static byte[] BuildCanonicalBytes(
        NeuronId via,
        BrainId senderBrain,
        NeuronId target,
        string callerAccount,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var viaBytes = Encoding.UTF8.GetBytes(via.ToString());
        var senderBytes = Encoding.UTF8.GetBytes(senderBrain.Value ?? string.Empty);
        var targetBytes = Encoding.UTF8.GetBytes(target.ToString());
        var accountBytes = Encoding.UTF8.GetBytes(callerAccount);

        var total =
            4 + DomainSeparator.Length +
            4 + viaBytes.Length +
            4 + senderBytes.Length +
            4 + targetBytes.Length +
            4 + accountBytes.Length +
            8 + 8;

        var buffer = new byte[total];
        var offset = 0;
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, DomainSeparator);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, viaBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, senderBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, targetBytes);
        BrainEnvelopeSealer.WriteLengthPrefixed(buffer, ref offset, accountBytes);
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), issuedAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset, 8), expiresAtUtc.ToUniversalTime().ToUnixTimeMilliseconds());
        return buffer;
    }
}

/// <summary>Wire payload sealed inside a request <see cref="BrainEnvelope"/>.</summary>
public sealed record RemoteInvocationRequest(
    NeuronId Target,
    RuntimeInvocation Call,
    string CallerAccount,
    string CallerPrincipal,
    BrainId CallerBrain,
    bool OneWay = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemoteViaProvenance? ViaProvenance = null);

/// <summary>Wire payload sealed inside a reply <see cref="BrainEnvelope"/>.</summary>
public sealed record RemoteInvocationResponse(
    JsonElement? Result = null,
    string? ErrorType = null,
    string? ErrorMessage = null);

/// <summary>
/// <see cref="IBrainTransport"/> implementation that serializes invocations into JSON, seals them with
/// <see cref="BrainEnvelopeSealer"/> (via a non-exportable <see cref="IBrainKeySigner"/> or <see cref="BrainKeyPair"/>),
/// and forwards the opaque <see cref="BrainEnvelope"/> through a pluggable relay delegate.
/// </summary>
public sealed class SealedRelayBrainTransport : IBrainTransport
{
    internal static readonly TimeSpan DefaultMaxClockSkew = BrainEnvelopeSealer.DefaultMaxClockSkew;

    private readonly BrainId? _localBrain;
    private readonly IBrainKeySigner _signer;
    private readonly Func<BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> _keyLookup;
    private readonly ICallerAmbient _callers;
    private readonly Func<BrainEnvelope, CancellationToken, Task<BrainEnvelope>> _relay;
    private readonly Func<BrainEnvelope, CancellationToken, Task>? _postRelay;
    private readonly Func<BrainId, bool>? _isRemote;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxClockSkew;
    private readonly BrainEnvelopeReplayCache _replayCache;
    private readonly Func<NeuronId, RuntimeInvocation, Caller, bool>? _includeSenderPublicKeys;

    /// <summary>Creates a sealed relay transport for <paramref name="localBrain"/> using an in-memory <see cref="BrainKeyPair"/>.</summary>
    public SealedRelayBrainTransport(
        BrainId localBrain,
        BrainKeyPair localKeys,
        Func<BrainId, BrainPublicKeys?> keyLookup,
        ICallerAmbient callers,
        Func<BrainEnvelope, CancellationToken, Task<BrainEnvelope>> relay,
        Func<BrainId, bool>? isRemote = null,
        TimeProvider? clock = null,
        TimeSpan? maxClockSkew = null,
        Func<BrainEnvelope, CancellationToken, Task>? postRelay = null,
        BrainEnvelopeReplayCache? replayCache = null,
        Func<NeuronId, RuntimeInvocation, Caller, bool>? includeSenderPublicKeys = null)
        : this(
            new BrainKeyPairSigner(localKeys),
            (brain, _) => ValueTask.FromResult(keyLookup(brain)),
            callers,
            relay,
            postRelay: postRelay,
            isRemote: isRemote,
            localBrain: localBrain,
            timeProvider: clock,
            maxClockSkew: maxClockSkew,
            replayCache: replayCache,
            includeSenderPublicKeys: includeSenderPublicKeys)
    {
        ArgumentNullException.ThrowIfNull(keyLookup);
    }

    /// <summary>
    /// Creates a sealed relay transport backed by a non-exportable <see cref="IBrainKeySigner"/>.
    /// When <paramref name="localBrain"/> is <see langword="null"/>, the sender brain is resolved per call from the ambient caller.
    /// </summary>
    public SealedRelayBrainTransport(
        IBrainKeySigner signer,
        Func<BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> keyLookup,
        ICallerAmbient callers,
        Func<BrainEnvelope, CancellationToken, Task<BrainEnvelope>> relay,
        Func<BrainEnvelope, CancellationToken, Task>? postRelay = null,
        Func<BrainId, bool>? isRemote = null,
        BrainId? localBrain = null,
        TimeProvider? timeProvider = null,
        TimeSpan? maxClockSkew = null,
        BrainEnvelopeReplayCache? replayCache = null,
        Func<NeuronId, RuntimeInvocation, Caller, bool>? includeSenderPublicKeys = null)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(keyLookup);
        ArgumentNullException.ThrowIfNull(callers);
        ArgumentNullException.ThrowIfNull(relay);

        var skew = maxClockSkew ?? DefaultMaxClockSkew;
        if (skew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxClockSkew), skew, "Maximum clock skew cannot be negative.");
        }

        _signer = signer;
        _keyLookup = keyLookup;
        _callers = callers;
        _relay = relay;
        _postRelay = postRelay;
        _isRemote = isRemote;
        _localBrain = localBrain;
        _clock = timeProvider ?? TimeProvider.System;
        _maxClockSkew = skew;
        var retention = skew > TimeSpan.Zero ? skew + skew : TimeSpan.FromMinutes(1);
        _replayCache = replayCache ?? new BrainEnvelopeReplayCache(retentionWindow: retention);
        _includeSenderPublicKeys = includeSenderPublicKeys;
    }

    /// <inheritdoc />
    public bool IsRemote(BrainId brain)
        => _isRemote is not null
            ? _isRemote(brain)
            : _localBrain is { } local && !brain.IsSystem && brain != local;

    /// <inheritdoc />
    public Task<JsonElement?> InvokeRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
        => SendAsync(target, call, oneWay: false, cancellationToken);

    /// <inheritdoc />
    public async Task PostRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
    {
        _ = await SendAsync(target, call, oneWay: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement?> SendAsync(NeuronId target, RuntimeInvocation call, bool oneWay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        var caller = _callers.Current
            ?? throw new UnauthorizedAccessException($"Remote call {call.Contract}.{call.Method} on {target}: no caller is established.");
        var person = caller.For
            ?? throw new UnauthorizedAccessException($"Remote call {call.Contract}.{call.Method} on {target}: caller has no person account.");

        var senderBrain = _localBrain ?? caller.Brain;
        var recipientKeys = await _keyLookup(target.Brain, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No public keys are known for remote brain '{target.Brain.Value}'.");

        var sentAtUtc = _clock.GetUtcNow();
        RemoteViaProvenance? viaProvenance = null;
        if (caller.Via is { } via)
        {
            var viaLifetime = _maxClockSkew > TimeSpan.Zero ? _maxClockSkew : TimeSpan.FromMinutes(1);
            if (viaLifetime > RemoteViaProvenanceSigner.MaxLifetime)
            {
                viaLifetime = RemoteViaProvenanceSigner.MaxLifetime;
            }

            viaProvenance = await RemoteViaProvenanceSigner.SignAsync(
                via,
                senderBrain,
                target,
                person.Account,
                sentAtUtc,
                sentAtUtc + viaLifetime,
                _signer,
                cancellationToken).ConfigureAwait(false);
        }

        var request = new RemoteInvocationRequest(target, call, person.Account, person.Principal, caller.Brain, oneWay, viaProvenance);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json);
        var messageId = Guid.NewGuid().ToString("N");

        var requestEnvelope = await BrainEnvelopeSealer.SealAsync(
            senderBrain,
            target.Brain,
            messageId,
            plaintext,
            _signer,
            recipientKeys,
            sentAtUtc: sentAtUtc,
            oneWay: oneWay,
            ct: cancellationToken).ConfigureAwait(false);

        if (_includeSenderPublicKeys is not null && _includeSenderPublicKeys(target, call, caller))
        {
            var myPublicKeys = await _signer.GetPublicKeysAsync(senderBrain, cancellationToken).ConfigureAwait(false);
            requestEnvelope = requestEnvelope with { SenderPublicKeys = myPublicKeys };
        }

        if (oneWay && _postRelay is not null)
        {
            await _postRelay(requestEnvelope, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var replyEnvelope = await _relay(requestEnvelope, cancellationToken).ConfigureAwait(false);
        if (replyEnvelope.From != target.Brain || replyEnvelope.To != senderBrain || !string.Equals(replyEnvelope.ReplyToMessageId, messageId, StringComparison.Ordinal))
        {
            throw new CryptographicException("Remote reply envelope metadata does not match the outgoing request.");
        }

        var now = _clock.GetUtcNow();
        ValidateFreshnessAndReplayPrecheck(replyEnvelope, now, _maxClockSkew, _replayCache);

        var replyPlaintext = await BrainEnvelopeSealer.OpenAsync(replyEnvelope, _signer, recipientKeys, cancellationToken).ConfigureAwait(false);
        if (!_replayCache.TryAccept(replyEnvelope.From, replyEnvelope.MessageId, now))
        {
            throw new CryptographicException("Duplicate reply BrainEnvelope MessageId detected (replay rejected).");
        }

        var response = JsonSerializer.Deserialize<RemoteInvocationResponse>(replyPlaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote reply payload was null.");

        if (response.ErrorType is not null)
        {
            ThrowRemoteError(response.ErrorType, response.ErrorMessage);
        }

        return response.Result;
    }

    internal static void ValidateFreshnessAndReplayPrecheck(
        BrainEnvelope envelope,
        DateTimeOffset nowUtc,
        TimeSpan maxClockSkew,
        BrainEnvelopeReplayCache replayCache)
    {
        if (string.IsNullOrWhiteSpace(envelope.MessageId))
        {
            throw new CryptographicException("BrainEnvelope MessageId must be non-empty.");
        }

        if (envelope.SentAtUtc is not { } sentAt || !BrainEnvelopeReceiver.IsWithinClockSkew(sentAt, nowUtc, maxClockSkew))
        {
            throw new CryptographicException("BrainEnvelope timestamp is missing or outside the allowed clock skew window.");
        }

        if (replayCache.Contains(envelope.From, envelope.MessageId, nowUtc))
        {
            throw new CryptographicException($"Duplicate BrainEnvelope message '{envelope.MessageId}' from '{envelope.From.Value}' rejected as replay.");
        }
    }

    private static void ThrowRemoteError(string errorType, string? message)
    {
        var text = message ?? errorType;
        throw errorType switch
        {
            nameof(UnauthorizedAccessException) => new UnauthorizedAccessException(text),
            nameof(ArgumentException) => new ArgumentException(text),
            nameof(InvalidOperationException) => new InvalidOperationException(text),
            nameof(BindingException) => new BindingException(text),
            nameof(CryptographicException) => new CryptographicException(text),
            _ => new InvalidOperationException($"{errorType}: {text}"),
        };
    }
}

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
        if (!_replayCache.TryAccept(envelope.From, envelope.MessageId, now))
        {
            throw new CryptographicException($"Duplicate BrainEnvelope message '{envelope.MessageId}' from '{envelope.From.Value}' rejected as replay.");
        }

        var request = JsonSerializer.Deserialize<RemoteInvocationRequest>(plaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote invocation payload was null.");

        if (isUnpinnedHandshake && !_allowUnpinnedHandshake!(envelope, request, senderKeys))
        {
            throw new CryptographicException($"Unpinned sender brain '{envelope.From.Value}' is not permitted to invoke '{request.Target}.{request.Call.Method}'.");
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
