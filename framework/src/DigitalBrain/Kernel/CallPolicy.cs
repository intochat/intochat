using System.Collections.Concurrent;
using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;

namespace DigitalBrain.Kernel;

/// <summary>
/// Domain rules for method call properties, interleaving rules ([InterleavesWhen]), reentrancy rules,
/// default timeouts, and call admission classification.
/// A collectible class is never cached in static dictionaries to prevent load context leaks.
/// </summary>
internal static class CallPolicy
{
    private static readonly ConcurrentDictionary<(Type Class, string Contract), Type?> Contracts = new();
    private static readonly ConcurrentDictionary<(Type Class, MethodInfo Method), bool> Methods = new();
    private static readonly ConcurrentDictionary<Type, Func<RuntimeInvocation, bool>?> Predicates = new();

    /// <summary>Binds a call to the contract interface on the neuron class.</summary>
    /// <exception cref="BindingException">The class does not implement the contract, or the call does not bind.</exception>
    public static CallDescriptor Bind(NeuronBinder binder, Type neuronClass, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(neuronClass);
        ArgumentNullException.ThrowIfNull(call);

        var contract = (neuronClass.IsCollectible
            ? FindContract((neuronClass, call.Contract))
            : Contracts.GetOrAdd((neuronClass, call.Contract), FindContract))
            ?? throw new BindingException($"{neuronClass.FullName} does not implement contract {call.Contract}.");

        return binder.Bind(contract, call);
    }

    /// <summary>Whether <paramref name="call"/> may run while another turn of its neuron is suspended.</summary>
    public static bool MayInterleave(Type neuronClass, CallDescriptor descriptor, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return MayInterleave(neuronClass, descriptor.Method, call);
    }

    /// <summary>Whether <paramref name="call"/> on <paramref name="method"/> may run while another turn of its neuron is suspended.</summary>
    public static bool MayInterleave(Type neuronClass, MethodInfo method, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(call);

        if (neuronClass.IsDefined(typeof(ReentrantAttribute), inherit: true)) { return true; }

        var isDeclaredInterleaving = neuronClass.IsCollectible
            ? Declared((neuronClass, method))
            : Methods.GetOrAdd((neuronClass, method), Declared);

        if (isDeclaredInterleaving) { return true; }

        if (PredicateOf(neuronClass) is not { } predicate) { return false; }
        try { return predicate(call); }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>The class's <see cref="InterleavesWhenAttribute"/> predicate, or null when it declares none.</summary>
    /// <exception cref="InvalidOperationException">The named predicate is not a static bool P(RuntimeInvocation) on the class or a base.</exception>
    public static Func<RuntimeInvocation, bool>? PredicateOf(Type neuronClass)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        return neuronClass.IsCollectible ? Resolve(neuronClass) : Predicates.GetOrAdd(neuronClass, Resolve);
    }

    /// <summary>Whether <paramref name="method"/> carries <see cref="OneWayCallAttribute"/>.</summary>
    public static bool IsOneWay(MethodInfo method) => IsOneWay(method, implementation: null);

    /// <summary>Whether the contract method or its implementation carries <see cref="OneWayCallAttribute"/>.</summary>
    internal static bool IsOneWay(MethodInfo method, MethodInfo? implementation)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Marked<OneWayCallAttribute>(method, implementation);
    }

    /// <summary>Gets the declared timeout override for the method, or null.</summary>
    public static TimeSpan? TimeoutOf(MethodInfo method) => TimeoutOf(method, implementation: null);

    /// <summary>The contract method's timeout override, else its implementation's, else null.</summary>
    internal static TimeSpan? TimeoutOf(MethodInfo method, MethodInfo? implementation)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method.GetCustomAttribute<CallTimeoutAttribute>()?.Timeout
            ?? implementation?.GetCustomAttribute<CallTimeoutAttribute>()?.Timeout;
    }

    /// <summary>Whether the contract method or its implementation carries <typeparamref name="TAttribute"/> (not inherited).</summary>
    internal static bool Marked<TAttribute>(MethodInfo contractMethod, MethodInfo? implementationMethod) where TAttribute : Attribute
        => contractMethod.IsDefined(typeof(TAttribute), false)
        || (implementationMethod?.IsDefined(typeof(TAttribute), false) ?? false);

    /// <summary>Resolves the access level required to invoke <paramref name="method"/> on <paramref name="contract"/>.</summary>
    public static OperationAccess AccessOf(Type contract, MethodInfo method)
        => AccessOf(contract, method, implementation: null);

    internal static OperationAccess AccessOf(Type contract, MethodInfo method, MethodInfo? implementation)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(method);

        var attr = method.GetCustomAttribute<AccessAttribute>(inherit: false)
            ?? implementation?.GetCustomAttribute<AccessAttribute>(inherit: false)
            ?? contract.GetCustomAttribute<AccessAttribute>(inherit: false);

        return attr switch
        {
            PublicAttribute => OperationAccess.Public,
            SharedAttribute => OperationAccess.Shared,
            InternalAttribute => OperationAccess.Internal,
            PerAccountAttribute => OperationAccess.Internal,
            _ => contract.IsVisible ? OperationAccess.Owner : OperationAccess.Internal,
        };
    }

    /// <summary>Finds the contract interface implemented by <paramref name="key"/>.Class matching name <paramref name="key"/>.Contract.</summary>
    public static Type? FindContract((Type Class, string Contract) key)
        => key.Class.GetInterfaces().FirstOrDefault(face => NeuronProxy.ContractName(face) == key.Contract);

    private static Func<RuntimeInvocation, bool>? Resolve(Type neuronClass)
    {
        if (neuronClass.GetCustomAttribute<InterleavesWhenAttribute>(inherit: true) is not { } when) { return null; }

        for (var level = neuronClass; level is not null && level != typeof(object); level = level.BaseType)
        {
            var method = level.GetMethod(
                when.Predicate,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                [typeof(RuntimeInvocation)]);

            if (method is null) { continue; }
            if (method.ReturnType != typeof(bool)) { break; }
            return method.CreateDelegate<Func<RuntimeInvocation, bool>>();
        }

        throw new InvalidOperationException($"{neuronClass.FullName} names [InterleavesWhen(\"{when.Predicate}\")] but has no static bool {when.Predicate}(RuntimeInvocation).");
    }

    internal static MethodInfo? FindImplementationMethod(Type targetClass, MethodInfo interfaceMethod)
    {
        var ifaceParams = interfaceMethod.GetParameters();
        var ifaceFullName = interfaceMethod.DeclaringType?.FullName;
        var explicitSuffix = "." + interfaceMethod.Name;

        for (var level = targetClass; level is not null && level != typeof(object); level = level.BaseType)
        {
            foreach (var candidate in level.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if ((ifaceFullName is not null && candidate.Name == ifaceFullName + explicitSuffix)
                    || candidate.Name.EndsWith(explicitSuffix, StringComparison.Ordinal))
                {
                    if (ParametersEqual(candidate.GetParameters(), ifaceParams))
                    {
                        return candidate;
                    }
                }
            }
        }

        for (var level = targetClass; level is not null && level != typeof(object); level = level.BaseType)
        {
            foreach (var candidate in level.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (candidate.Name == interfaceMethod.Name && ParametersEqual(candidate.GetParameters(), ifaceParams))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static bool IsDisposeMethod(MethodInfo method)
    {
        if (method.GetParameters().Length != 0)
        {
            return false;
        }
        return (method.Name == nameof(IDisposable.Dispose) && method.ReturnType == typeof(void))
            || (method.Name == nameof(IAsyncDisposable.DisposeAsync) && method.ReturnType == typeof(ValueTask));
    }

    private static bool ParametersEqual(ParameterInfo[] left, ParameterInfo[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].ParameterType != right[i].ParameterType)
            {
                return false;
            }
        }
        return true;
    }

    private static bool Declared((Type Class, MethodInfo Method) key)
    {
        if (Marked(key.Method)) { return true; }
        if (key.Method.DeclaringType is { IsInterface: true } contract && contract.IsAssignableFrom(key.Class))
        {
            return FindImplementationMethod(key.Class, key.Method) is { } impl && Marked(impl);
        }

        var classParams = key.Method.GetParameters();
        foreach (var iface in key.Class.GetInterfaces())
        {
            foreach (var ifaceMethod in iface.GetMethods())
            {
                if ((key.Method.Name == ifaceMethod.Name || key.Method.Name.EndsWith("." + ifaceMethod.Name, StringComparison.Ordinal))
                    && ParametersEqual(ifaceMethod.GetParameters(), classParams)
                    && Marked(ifaceMethod))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Marked(MethodInfo method)
        => method.IsDefined(typeof(InterleavesAttribute), false) || method.IsDefined(typeof(ReadOnlyCallAttribute), false);
}
