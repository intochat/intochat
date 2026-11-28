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
