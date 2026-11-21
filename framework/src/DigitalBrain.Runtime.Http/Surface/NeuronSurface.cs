using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;

namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Description of a neuron instance.
/// </summary>
public sealed record NeuronInstance(string Contract, string Key, DateTimeOffset Created, string? Creator);

/// <summary>
/// Provides neuron discovery and invocation surface over a runtime dispatcher.
/// </summary>
public sealed class NeuronSurface(IRuntimeInvocationDispatcher dispatcher, IReadOnlySet<string>? allowed = null)
{
    public NeuronSurface Restrict(IReadOnlyCollection<string> names)
        => new(dispatcher, names.ToHashSet(StringComparer.Ordinal));

    public bool Allowed(string contract)
        => allowed is null || allowed.Contains(contract);

    public static Task<string> Get(string contract, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var type = LoadedContracts.Find(contract);
        return Task.FromResult(PlainNeuronContracts.IdOf(type, key).ToString());
    }

    public async Task<JsonElement?> Call(string brainId, string contract, string key, string method, string arguments, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var type = LoadedContracts.Find(contract);
        ArrangedCall arranged;
        try
        {
            using var parsed = string.IsNullOrWhiteSpace(arguments) ? null : JsonDocument.Parse(arguments);
            arranged = NeuronBinder.Arrange(type, method, parsed is null ? default : parsed.RootElement);
        }
        catch (JsonException error)
        {
            throw new ArgumentException("arguments must be a JSON object or array: " + error.Message, nameof(arguments), error);
        }
        catch (BindingException error)
        {
            throw new ArgumentException(error.Message, nameof(arguments), error);
        }

        var invocation = new RuntimeInvocation(
            Contract: contract,
            Key: key,
            Method: method,
            Arguments: arranged.Arguments,
            Behavior: string.Empty);

        return await dispatcher.DispatchAsync(brainId, invocation, ct).ConfigureAwait(false);
    }
}
