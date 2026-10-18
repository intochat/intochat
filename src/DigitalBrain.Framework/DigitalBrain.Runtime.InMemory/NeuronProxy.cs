using System.Collections.Concurrent;
using System.Reflection;

namespace DigitalBrain.Runtime.InMemory;

// A contract reference: every interface call goes straight to the address's host, with no serialization.
// Two references to one address are equal, so Unwatch finds the observer Watch registered.
public class NeuronProxy : DispatchProxy
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> Casts = new();
    private static readonly MethodInfo CastMethod = typeof(NeuronProxy).GetMethod(nameof(Cast), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo CastValueMethod = typeof(NeuronProxy).GetMethod(nameof(CastValue), BindingFlags.NonPublic | BindingFlags.Static)!;

    private NeuronHost? _host;

    internal static T Create<T>(NeuronHost host) where T : class
    {
        var proxy = Create<T, NeuronProxy>();
        ((NeuronProxy)(object)proxy)._host = host;
        return proxy;
    }

    public override bool Equals(object? obj) => obj is NeuronProxy other && ReferenceEquals(other._host, _host);

    public override int GetHashCode() => _host?.Address.GetHashCode() ?? 0;

    public override string ToString() => _host?.Address.StorageKey ?? base.ToString()!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var call = _host!.Call(targetMethod, args ?? []);
        var returns = targetMethod.ReturnType;
        if (returns == typeof(Task)) { return call; }
        if (returns == typeof(ValueTask)) { return new ValueTask(call); }
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
        { return Casts.GetOrAdd(returns, r => CastMethod.MakeGenericMethod(r.GetGenericArguments()[0])).Invoke(null, [call]); }
        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(ValueTask<>))
        { return Casts.GetOrAdd(returns, r => CastValueMethod.MakeGenericMethod(r.GetGenericArguments()[0])).Invoke(null, [call]); }
        throw new NotSupportedException($"{targetMethod.DeclaringType?.Name}.{targetMethod.Name} returns {returns.Name}; neuron calls return Task or ValueTask.");
    }

    private static async Task<T> Cast<T>(Task<object?> call) => (T)(await call)!;

    private static ValueTask<T> CastValue<T>(Task<object?> call) => new(Cast<T>(call));
}
