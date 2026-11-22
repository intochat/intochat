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
/// <see cref="BrainEnvelopeSealer"/>, and forwards the opaque <see cref="BrainEnvelope"/> through a pluggable relay delegate.
/// </summary>
public sealed class SealedRelayBrainTransport : IBrainTransport
{
    private readonly BrainId _localBrain;
    private readonly BrainKeyPair _localKeys;
    private readonly Func<BrainId, BrainPublicKeys?> _keyLookup;
    private readonly ICallerAmbient _callers;
    private readonly Func<BrainEnvelope, CancellationToken, Task<BrainEnvelope>> _relay;
    private readonly Func<BrainId, bool>? _isRemote;

    /// <summary>Creates a sealed relay transport for <paramref name="localBrain"/>.</summary>
    public SealedRelayBrainTransport(
        BrainId localBrain,
        BrainKeyPair localKeys,
        Func<BrainId, BrainPublicKeys?> keyLookup,
        ICallerAmbient callers,
        Func<BrainEnvelope, CancellationToken, Task<BrainEnvelope>> relay,
        Func<BrainId, bool>? isRemote = null)
    {
        ArgumentNullException.ThrowIfNull(localKeys);
        ArgumentNullException.ThrowIfNull(keyLookup);
        ArgumentNullException.ThrowIfNull(callers);
        ArgumentNullException.ThrowIfNull(relay);

        _localBrain = localBrain;
        _localKeys = localKeys;
        _keyLookup = keyLookup;
        _callers = callers;
        _relay = relay;
        _isRemote = isRemote;
    }

    /// <inheritdoc />
    public bool IsRemote(BrainId brain)
        => _isRemote is not null ? _isRemote(brain) : !brain.IsSystem && brain != _localBrain;

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

        var recipientKeys = _keyLookup(target.Brain)
            ?? throw new InvalidOperationException($"No public keys are known for remote brain '{target.Brain.Value}'.");

        var request = new RemoteInvocationRequest(target, call, person.Account, person.Principal, caller.Brain, oneWay);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json);
        var messageId = Guid.NewGuid().ToString("N");

        var requestEnvelope = BrainEnvelopeSealer.Seal(
            _localBrain,
            target.Brain,
            messageId,
            plaintext,
            _localKeys,
            recipientKeys);

        var replyEnvelope = await _relay(requestEnvelope, cancellationToken).ConfigureAwait(false);
        if (replyEnvelope.From != target.Brain || replyEnvelope.To != _localBrain || !string.Equals(replyEnvelope.ReplyToMessageId, messageId, StringComparison.Ordinal))
        {
            throw new CryptographicException("Remote reply envelope metadata does not match the outgoing request.");
        }

        var replyPlaintext = BrainEnvelopeSealer.Open(replyEnvelope, _localKeys, recipientKeys);
        var response = JsonSerializer.Deserialize<RemoteInvocationResponse>(replyPlaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote reply payload was null.");

        if (response.ErrorType is not null)
        {
            ThrowRemoteError(response.ErrorType, response.ErrorMessage);
        }

        return response.Result;
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
/// Receiving side of cross-cluster transport: verifies and decrypts an incoming <see cref="BrainEnvelope"/>,
/// stamps the foreign person caller through <see cref="EdgeAuthority"/>, dispatches through the local host's
/// unchanged <see cref="AccessGate"/> and neuron host, and returns a sealed reply <see cref="BrainEnvelope"/>.
/// </summary>
public sealed class BrainEnvelopeReceiver
{
    private readonly BrainId _localBrain;
    private readonly BrainKeyPair _localKeys;
    private readonly Func<BrainId, BrainPublicKeys?> _keyLookup;
    private readonly EdgeAuthority _edge;
    private readonly Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> _invoke;
    private readonly Func<NeuronId, RuntimeInvocation, Task>? _post;

    /// <summary>Creates a receiver for <paramref name="localBrain"/>.</summary>
    public BrainEnvelopeReceiver(
        BrainId localBrain,
        BrainKeyPair localKeys,
        Func<BrainId, BrainPublicKeys?> keyLookup,
        EdgeAuthority edge,
        Func<NeuronId, RuntimeInvocation, CancellationToken, Task<JsonElement?>> invoke,
        Func<NeuronId, RuntimeInvocation, Task>? post = null)
    {
        ArgumentNullException.ThrowIfNull(localKeys);
        ArgumentNullException.ThrowIfNull(keyLookup);
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(invoke);

        _localBrain = localBrain;
        _localKeys = localKeys;
        _keyLookup = keyLookup;
        _edge = edge;
        _invoke = invoke;
        _post = post;
    }

    /// <summary>
    /// Verifies the sender's signature, decrypts the request, dispatches it locally under the authenticated foreign caller,
    /// and seals the result or error into a reply <see cref="BrainEnvelope"/>.
    /// </summary>
    /// <exception cref="CryptographicException">Thrown when the envelope fails signature verification, decryption, or routing checks.</exception>
    public async Task<BrainEnvelope> DeliverAsync(BrainEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.To != _localBrain)
        {
            throw new CryptographicException($"Envelope target '{envelope.To.Value}' does not match local brain '{_localBrain.Value}'.");
        }

        var senderKeys = _keyLookup(envelope.From)
            ?? throw new CryptographicException($"No public keys are known for sender brain '{envelope.From.Value}'.");

        var plaintext = BrainEnvelopeSealer.Open(envelope, _localKeys, senderKeys);
        var request = JsonSerializer.Deserialize<RemoteInvocationRequest>(plaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Remote invocation payload was null.");

        RemoteInvocationResponse response;
        if (request.CallerBrain != envelope.From || request.Target.Brain != _localBrain)
        {
            response = new RemoteInvocationResponse(
                ErrorType: nameof(UnauthorizedAccessException),
                ErrorMessage: $"Remote invocation routing mismatch: envelope from '{envelope.From.Value}' to '{_localBrain.Value}' carried caller brain '{request.CallerBrain.Value}' for target '{request.Target}'.");
        }
        else
        {
            using var _ = _edge.Enter(_edge.Person(request.CallerPrincipal, request.CallerAccount, envelope.From));
            try
            {
                if (request.OneWay && _post is not null)
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
        return BrainEnvelopeSealer.Seal(
            _localBrain,
            envelope.From,
            Guid.NewGuid().ToString("N"),
            replyPlaintext,
            _localKeys,
            senderKeys,
            replyToMessageId: envelope.MessageId);
    }
}
