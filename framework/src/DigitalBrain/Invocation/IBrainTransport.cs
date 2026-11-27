using System.Security.Cryptography;
using System.Text.Json;
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

/// <summary>Wire payload sealed inside a request <see cref="BrainEnvelope"/>.</summary>
public sealed record RemoteInvocationRequest(
    NeuronId Target,
    RuntimeInvocation Call,
    string CallerAccount,
    string CallerPrincipal,
    BrainId CallerBrain,
    bool OneWay = false);

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
        BrainEnvelopeReplayCache? replayCache = null)
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
            replayCache: replayCache)
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
        BrainEnvelopeReplayCache? replayCache = null)
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

        var request = new RemoteInvocationRequest(target, call, person.Account, person.Principal, caller.Brain, oneWay);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json);
        var messageId = Guid.NewGuid().ToString("N");
        var sentAtUtc = _clock.GetUtcNow();

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
    private readonly Func<BrainId, CancellationToken, ValueTask<BrainPublicKeys?>> _keyLookup;
    private readonly EdgeAuthority _edge;
    private readonly Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> _invoke;
    private readonly Func<NeuronId, RuntimeInvocation, Task>? _post;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _maxClockSkew;
    private readonly BrainEnvelopeReplayCache _replayCache;
    private readonly Func<BrainId, string, CancellationToken, ValueTask<bool>>? _verifyCallerAccount;

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
        BrainEnvelopeReplayCache? replayCache = null)
        : this(
            brain => brain == localBrain,
            new BrainKeyPairSigner(localKeys),
            (brain, _) => ValueTask.FromResult(keyLookup(brain)),
            edge,
            invoke,
            post: post,
            timeProvider: clock,
            maxClockSkew: maxClockSkew,
            verifyCallerAccount: verifyCallerAccount,
            replayCache: replayCache)
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
        BrainEnvelopeReplayCache? replayCache = null)
    {
        ArgumentNullException.ThrowIfNull(isLocalBrain);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(keyLookup);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(invoke);

        var skew = maxClockSkew ?? SealedRelayBrainTransport.DefaultMaxClockSkew;
        if (skew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxClockSkew), skew, "Maximum clock skew cannot be negative.");
        }

        _isLocalBrain = isLocalBrain;
        _signer = signer;
        _keyLookup = keyLookup;
        _edge = edge;
        _invoke = invoke;
        _post = post;
        _clock = timeProvider ?? TimeProvider.System;
        _maxClockSkew = skew;
        _verifyCallerAccount = verifyCallerAccount;
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

        var senderKeys = await _keyLookup(envelope.From, cancellationToken).ConfigureAwait(false)
            ?? throw new CryptographicException($"No public keys are known for sender brain '{envelope.From.Value}'.");

        var plaintext = await BrainEnvelopeSealer.OpenAsync(envelope, _signer, senderKeys, cancellationToken).ConfigureAwait(false);
        if (!_replayCache.TryAccept(envelope.From, envelope.MessageId, now))
        {
            throw new CryptographicException($"Duplicate BrainEnvelope message '{envelope.MessageId}' from '{envelope.From.Value}' rejected as replay.");
        }

        var request = JsonSerializer.Deserialize<RemoteInvocationRequest>(plaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote invocation payload was null.");

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
        else if (_verifyCallerAccount is not null && !await _verifyCallerAccount(envelope.From, request.CallerAccount, cancellationToken).ConfigureAwait(false))
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: $"Caller account '{request.CallerAccount}' is not authorized for sender brain '{envelope.From.Value}'.");
        }
        else
        {
            using var _ = _edge.Enter(_edge.Person(request.CallerPrincipal, request.CallerAccount, envelope.From));
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
