using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Access;

namespace DigitalBrain.Invocation;

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
