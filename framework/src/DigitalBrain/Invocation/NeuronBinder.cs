using System.Runtime.InteropServices;
using System.Text.Json;
using DigitalBrain.Kernel;

namespace DigitalBrain.Invocation;

/// <summary>
/// Binds a <see cref="RuntimeInvocation"/> to the contract method it names, through the contract's <see cref="CallCatalog"/>: by name,
/// argument count and, for overloads, argument shape. It adds the one rule the catalog does not hold, the bound on argument size.
/// </summary>
internal sealed class NeuronBinder(int maximumArgumentBytes = NeuronBinder.DefaultMaximumArgumentBytes)
{
    public const int DefaultMaximumArgumentBytes = 1024 * 1024;

    public int MaximumArgumentBytes { get; } = maximumArgumentBytes > 0
        ? maximumArgumentBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumArgumentBytes));

    /// <exception cref="BindingException">No method, more than one, wrong arguments or arguments over the bound.</exception>
    public CallDescriptor Bind(Type contract, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (call is null || call.Method is null || call.Arguments is null) { throw new BindingException($"{contract.Name}: an invocation needs a method and arguments."); }
        long bytes = 0;
        foreach (var argument in call.Arguments)
        {
            if (argument.ValueKind == JsonValueKind.Undefined) { throw new BindingException($"{contract.Name}.{call.Method}: an argument is missing."); }
            bytes += JsonMarshal.GetRawUtf8Value(argument).Length;
        }
        if (bytes > MaximumArgumentBytes) { throw new BindingException($"{contract.Name}.{call.Method}: arguments of {bytes} bytes exceed the bound of {MaximumArgumentBytes}."); }

        var candidates = CallCatalog.GetOrCreate(contract).GetCandidates(call.Method, call.Arguments.Length);
        if (candidates.Count == 0) { throw new BindingException($"{contract.Name} has no method {call.Method} taking {call.Arguments.Length} arguments."); }
        if (candidates.Count == 1) { return candidates[0]; }

        var matching = candidates.Where(candidate => Accepts(candidate, call.Arguments)).Take(2).ToArray();
        return matching.Length switch
        {
            1 => matching[0],
            0 => throw new BindingException($"{contract.Name}.{call.Method}: the arguments match no overload."),
            _ => throw new BindingException($"{contract.Name}.{call.Method}: the arguments match more than one overload."),
        };
    }

    private static bool Accepts(CallDescriptor candidate, JsonElement[] arguments)
    {
        var argument = 0;
        foreach (var type in candidate.ParameterTypes)
        {
            if (type == typeof(CancellationToken)) { continue; }
            try { _ = RuntimeProtocol.Deserialize(arguments[argument++], type); }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { return false; }
        }
        return true;
    }
}
