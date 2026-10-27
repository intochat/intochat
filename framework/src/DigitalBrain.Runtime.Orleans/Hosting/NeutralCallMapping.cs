using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Invocation;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>
/// How <c>neuron.host</c> honours the neutral scheduling attributes. Orleans reads its own interleaving attributes only on grain
/// methods, so the host grain's <c>[MayInterleave]</c> predicate reads the <see cref="RuntimeInvocation"/> argument instead:
/// <list type="bullet">
/// <item><see cref="InterleavesAttribute"/> and <see cref="ReadOnlyCallAttribute"/> on the contract or class method interleave fully
/// (a read-only call interleaves with every turn, as in TestOS).</item>
/// <item><see cref="InterleavesWhenAttribute"/>, inherited, names <c>static bool P(RuntimeInvocation)</c>; the nearest declaration wins.</item>
/// <item><see cref="ReentrantAttribute"/> classes are hosted by <c>neuron.host.reentrant</c>; the predicate also says yes for them.</item>
/// </list>
/// <see cref="OneWayCallAttribute"/> needs no mapping here: the proxy sends it through the one-way <c>Post</c>, scheduled like Invoke.
/// </summary>
internal static class NeutralCallMapping
{
    private static readonly ConcurrentDictionary<(Type Class, MethodInfo Method), bool> Methods = new();
    private static readonly ConcurrentDictionary<Type, Func<RuntimeInvocation, bool>?> Predicates = new();

    /// <summary>The body of the host grain's <c>[MayInterleave]</c> predicate.</summary>
    public static bool MayInterleave(IInvokable request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.GetArgumentCount() > 0 && request.GetArgument(0) is RuntimeInvocation call && MayInterleave(call);
    }

    /// <summary>Whether <paramref name="call"/> may run while another turn of its neuron is suspended.</summary>
    public static bool MayInterleave(RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!NeuronId.TryParse(call.Key, out var id) || !NeuronTypeRegistry.TryGetAny(id.Type, out var neuronClass)) { return false; }
        if (neuronClass.IsDefined(typeof(ReentrantAttribute), inherit: true)) { return true; }
        BoundCall bound;
        try { bound = NeuronHostGrainBase.Bind(neuronClass, call); }
        catch (BindingException) { return false; } // the call itself refuses it, inside a turn
        if (Methods.GetOrAdd((neuronClass, bound.Method), static key => Declared(key.Class, key.Method))) { return true; }
        if (PredicateOf(neuronClass) is not { } predicate) { return false; }
        try { return predicate(call); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>The class's <see cref="InterleavesWhenAttribute"/> predicate, or null when it declares none.</summary>
    /// <exception cref="InvalidOperationException">The named predicate is not a <c>static bool P(RuntimeInvocation)</c> on the class or a base.</exception>
    public static Func<RuntimeInvocation, bool>? PredicateOf(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        return Predicates.GetOrAdd(neuronClass, Resolve);
    }

    private static Func<RuntimeInvocation, bool>? Resolve(Type neuronClass)
    {
        if (neuronClass.GetCustomAttribute<InterleavesWhenAttribute>(inherit: true) is not { } when) { return null; }
        for (var level = neuronClass; level is not null && level != typeof(object); level = level.BaseType)
        {
            var method = level.GetMethod(when.Predicate, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly, [typeof(RuntimeInvocation)]);
            if (method is null) { continue; }
            if (method.ReturnType != typeof(bool)) { break; }
            return method.CreateDelegate<Func<RuntimeInvocation, bool>>();
        }
        throw new InvalidOperationException($"{neuronClass.FullName} names [InterleavesWhen(\"{when.Predicate}\")] but has no static bool {when.Predicate}(RuntimeInvocation).");
    }

    private static bool Declared(Type neuronClass, MethodInfo contractMethod)
    {
        List<MethodInfo> methods = [contractMethod];
        if (contractMethod.DeclaringType is { IsInterface: true } contract && contract.IsAssignableFrom(neuronClass))
        {
            var map = neuronClass.GetInterfaceMap(contract);
            var index = Array.IndexOf(map.InterfaceMethods, contractMethod);
            if (index >= 0) { methods.Add(map.TargetMethods[index]); }
        }
        return methods.Any(method => method.IsDefined(typeof(InterleavesAttribute), false) || method.IsDefined(typeof(ReadOnlyCallAttribute), false));
    }
}
