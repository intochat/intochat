using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// Routes calls using the pure domain kernel rules in <see cref="CallPolicy"/> and maps neutral scheduling attributes onto Orleans.
/// </summary>
public static class NeutralCallMapping
{
    private sealed record Markers(bool Interleaves, bool OneWay, TimeSpan? Timeout);

    private static readonly ConcurrentDictionary<MethodInfo, Markers> Methods = new();
    private static readonly ConcurrentDictionary<Type, Func<MethodInfo, IReadOnlyList<object?>, bool>?> LegacyPredicates = new();

    /// <summary>Whether <paramref name="call"/> may run while another turn of its neuron is suspended.</summary>
    public static bool MayInterleave(Type neuronClass, BoundCall bound, RuntimeInvocation call)
        => CallPolicy.MayInterleave(neuronClass, bound, call);

    /// <summary>Orleans <c>[MayInterleave]</c> predicate reading neutral attributes from the incoming <see cref="IInvokable"/> request.</summary>
    public static bool MayInterleave(Type neuronClass, IInvokable request)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        ArgumentNullException.ThrowIfNull(request);
        var method = request.GetMethod();
        if (method is not null && Of(method).Interleaves) { return true; }
        if (LegacyPredicates.GetOrAdd(neuronClass, LegacyPredicateOf) is not { } predicate) { return false; }
        var arguments = new object?[request.GetArgumentCount()];
        for (var i = 0; i < arguments.Length; i++) { arguments[i] = request.GetArgument(i); }
        return method is not null && predicate(method, arguments);
    }

    /// <summary>The class's <see cref="InterleavesWhenAttribute"/> predicate, or null when it declares none.</summary>
    public static Func<RuntimeInvocation, bool>? PredicateOf(Type neuronClass)
        => CallPolicy.PredicateOf(neuronClass);

    /// <summary>Whether <paramref name="method"/> carries <see cref="OneWayCallAttribute"/>.</summary>
    public static bool IsOneWay(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Of(method).OneWay;
    }

    /// <summary>The declared <see cref="CallTimeoutAttribute"/> timeout on <paramref name="method"/>, or null.</summary>
    public static TimeSpan? TimeoutOf(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Of(method).Timeout;
    }

    private static Markers Of(MethodInfo method) => Methods.GetOrAdd(method, static m => new(
        m.IsDefined(typeof(InterleavesAttribute), false) || m.IsDefined(typeof(ReadOnlyCallAttribute), false),
        m.IsDefined(typeof(OneWayCallAttribute), false),
        m.GetCustomAttribute<CallTimeoutAttribute>(false) is { } timeout ? timeout.Timeout : null));

    // [InterleavesWhen(name)] names a static bool name(MethodInfo method, IReadOnlyList<object?> arguments) on the class.
    private static Func<MethodInfo, IReadOnlyList<object?>, bool>? LegacyPredicateOf(Type neuronClass)
    {
        if (neuronClass.GetCustomAttribute<InterleavesWhenAttribute>(false) is not { } when) { return null; }
        var method = neuronClass.GetMethod(when.Predicate, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(MethodInfo), typeof(IReadOnlyList<object?>)])
            ?? throw new InvalidOperationException($"{neuronClass.FullName} names interleaving predicate {when.Predicate}, which is not a static bool {when.Predicate}(MethodInfo, IReadOnlyList<object?>).");
        return method.ReturnType == typeof(bool)
            ? method.CreateDelegate<Func<MethodInfo, IReadOnlyList<object?>, bool>>()
            : throw new InvalidOperationException($"{neuronClass.FullName}.{when.Predicate} must return bool.");
    }
}
