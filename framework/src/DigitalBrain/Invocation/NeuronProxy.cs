using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Contracts;

namespace DigitalBrain.Invocation;

/// <summary>
/// The typed <c>Get&lt;T&gt;(NeuronId)</c> proxy: every call on the contract becomes one <see cref="RuntimeInvocation"/> handed to an
/// <see cref="IInvocationSink"/>. One-way calls post, streams stream, everything else invokes; <see cref="CallTimeoutAttribute"/> bounds the wait.
/// </summary>
/// <remarks>DispatchProxy needs a public, non-sealed type with a public constructor; create instances with <see cref="Create{T}"/>.</remarks>
#pragma warning disable CA1852
public class NeuronProxy : DispatchProxy
#pragma warning restore CA1852
{
    private static readonly ConcurrentDictionary<Type, string> ContractNames = new();
    private static readonly ConcurrentDictionary<Type, Func<NeuronId, IInvocationSink, object>> Factories = new();
    private static readonly ConcurrentDictionary<MethodInfo, ProxyMethod> Methods = new();
    private static readonly MethodInfo InvokeValueMethod = typeof(NeuronProxy).GetMethod(nameof(InvokeValue), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo InvokeValueTaskMethod = typeof(NeuronProxy).GetMethod(nameof(InvokeValueTask), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo StreamMethod = typeof(NeuronProxy).GetMethod(nameof(StreamItems), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private IInvocationSink _sink = null!;
    private string _contract = "";
    private string _key = "";

    /// <summary>The neuron this proxy calls.</summary>
    public NeuronId Target { get; private set; }

    /// <summary>Registers a compile-time proxy factory for contract <typeparamref name="T"/>.</summary>
    public static void Register<T>(Func<NeuronId, IInvocationSink, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Factories[typeof(T)] = (id, sink) => factory(id, sink);
    }

    /// <summary>Initializes a generated <see cref="NeuronProxy"/> subclass for contract <typeparamref name="T"/>.</summary>
    public static T Initialize<T>(NeuronProxy proxy, NeuronId id, IInvocationSink sink) where T : class
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(sink);
        proxy._sink = sink;
        proxy._contract = ContractName(typeof(T));
        proxy.Target = id;
        proxy._key = id.ToString();
        return (T)(object)proxy;
    }

    /// <summary>A proxy for contract <paramref name="contract"/> on neuron <paramref name="id"/> that hands calls to <paramref name="sink"/>.</summary>
    public static object Create(Type contract, NeuronId id, IInvocationSink sink)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(sink);
        if (!contract.IsInterface) { throw new ArgumentException($"{contract.Name} is not an interface; neuron contracts are interfaces.", nameof(contract)); }
        if (Factories.TryGetValue(contract, out var factory))
        {
            return factory(id, sink);
        }
        var generic = typeof(NeuronProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Create) && m.IsGenericMethodDefinition);
        return generic.MakeGenericMethod(contract).Invoke(null, [id, sink])!;
    }

    /// <summary>A proxy for contract <typeparamref name="T"/> on neuron <paramref name="id"/> that hands calls to <paramref name="sink"/>.</summary>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is not an interface.</exception>
    public static T Create<T>(NeuronId id, IInvocationSink sink) where T : class
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!typeof(T).IsInterface) { throw new ArgumentException($"{typeof(T).Name} is not an interface; neuron contracts are interfaces.", nameof(T)); }
        if (Factories.TryGetValue(typeof(T), out var factory))
        {
            return (T)factory(id, sink);
        }
        var proxy = Create<T, NeuronProxy>();
        return Initialize<T>((NeuronProxy)(object)proxy, id, sink);
    }

    /// <summary>The wire name of a contract: its <see cref="NeuronAliasAttribute"/> alias, or its full name.</summary>
    public static string ContractName(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        // Never cache collectible contracts in static dictionaries; doing so pins their AssemblyLoadContext.
        return contract.IsCollectible
            ? ReadContractName(contract)
            : ContractNames.GetOrAdd(contract, static type => ReadContractName(type));
    }

    private static string ReadContractName(Type contract)
        => contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias ?? contract.FullName ?? contract.Name;

    /// <summary>Two proxies for one neuron are equal.</summary>
    public override bool Equals(object? obj) => obj is NeuronProxy other && Target == other.Target && _contract == other._contract;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Target, _contract);

    /// <inheritdoc />
    public override string ToString() => $"{_contract}@{Target}";

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.DeclaringType == typeof(object))
        {
            return targetMethod.Name switch
            {
                nameof(Equals) => Equals(args?[0]),
                nameof(GetHashCode) => GetHashCode(),
                nameof(ToString) => ToString(),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }
        // Bound once per contract method: parameter types, token position, timeout and the dispatch delegate; no per-call reflection.
        var (bound, call) = Prepare(targetMethod, args);
        return bound.Dispatch(this, call, bound.Shape, bound.TokenOf(args));
    }

    private sealed class ProxyMethod
    {
        private readonly Type[] _parameters;
        private readonly int _token = -1;
        private readonly MethodInfo _method;
        private Func<NeuronProxy, RuntimeInvocation, CallShape, CancellationToken, object?>? _dispatch;

        public ProxyMethod(MethodInfo method)
        {
            _method = method;
            _parameters = [.. method.GetParameters().Select(parameter => parameter.ParameterType)];
            _token = Array.IndexOf(_parameters, typeof(CancellationToken));
            ArgumentCount = _parameters.Count(type => type != typeof(CancellationToken));
            Shape = new CallShape(method.GetCustomAttribute<CallTimeoutAttribute>()?.Timeout, method.Name);
        }

        public int ArgumentCount { get; }

        public CallShape Shape { get; }

        // Built on first DispatchProxy use only; generated subclasses never touch it, so AOT paths stay free of MakeGenericMethod.
        // A race only builds the same delegate twice.
        public Func<NeuronProxy, RuntimeInvocation, CallShape, CancellationToken, object?> Dispatch
            => _dispatch ??= NeuronProxy.Dispatch(_method);

        public JsonElement[] Encode(object?[]? args)
        {
            if (ArgumentCount == 0) { return []; }
            var arguments = new JsonElement[ArgumentCount];
            for (int index = 0, argument = 0; index < _parameters.Length; index++)
            {
                if (_parameters[index] == typeof(CancellationToken)) { continue; }
                arguments[argument++] = PooledJson.Encode(args is not null && index < args.Length ? args[index] : null, _parameters[index]);
            }
            return arguments;
        }

        public CancellationToken TokenOf(object?[]? args)
            => _token >= 0 && args is not null && _token < args.Length && args[_token] is CancellationToken token ? token : CancellationToken.None;
    }

    private static Func<NeuronProxy, RuntimeInvocation, CallShape, CancellationToken, object?> Dispatch(MethodInfo method)
    {
        var returnType = method.ReturnType;
        var definition = returnType.IsGenericType ? returnType.GetGenericTypeDefinition() : null;
        if (method.GetCustomAttribute<OneWayCallAttribute>() is not null)
        {
            if (returnType == typeof(Task)) { return static (proxy, call, _, _) => proxy._sink.Post(proxy.Target, call); }
            if (returnType == typeof(ValueTask)) { return static (proxy, call, _, _) => new ValueTask(proxy._sink.Post(proxy.Target, call)); }
            if (returnType == typeof(void)) { return static (proxy, call, _, _) => { _ = proxy._sink.Post(proxy.Target, call); return null; }; }
            throw new NotSupportedException($"{method.DeclaringType?.Name}.{method.Name} is one-way and so returns void, Task or ValueTask.");
        }
        if (returnType == typeof(Task)) { return static (proxy, call, shape, token) => proxy.InvokeNone(call, shape, token); }
        if (returnType == typeof(ValueTask)) { return static (proxy, call, shape, token) => new ValueTask(proxy.InvokeNone(call, shape, token)); }
        if (definition == typeof(Task<>)) { return Generic(InvokeValueMethod, returnType); }
        if (definition == typeof(ValueTask<>)) { return Generic(InvokeValueTaskMethod, returnType); }
        if (definition == typeof(IAsyncEnumerable<>)) { return Generic(StreamMethod, returnType); }
        throw new NotSupportedException($"{method.DeclaringType?.Name}.{method.Name} returns {returnType.Name}; neuron calls return Task, Task<T>, ValueTask, ValueTask<T> or IAsyncEnumerable<T>.");
    }

    private static Func<NeuronProxy, RuntimeInvocation, CallShape, CancellationToken, object?> Generic(MethodInfo open, Type returnType)
    {
        var closed = open.MakeGenericMethod(returnType.GetGenericArguments()[0])
            .CreateDelegate<Func<NeuronProxy, RuntimeInvocation, CallShape, CancellationToken, object?>>();
        return closed;
    }

    private async Task<JsonElement?> Send(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
    {
        if (shape.Timeout is not { } timeout) { return await _sink.Invoke(Target, call, cancellationToken).ConfigureAwait(false); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { return await _sink.Invoke(Target, call, limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{_contract}.{shape.Method} on {Target} did not answer within {timeout}.", error);
        }
    }

    /// <summary>Executes a compile-time generated one-way call without reflection or <c>MakeGenericMethod</c>.</summary>
    public Task CallOneWay(MethodInfo method, object?[] args)
        => _sink.Post(Target, Prepare(method, args).Call);

    /// <summary>Executes a compile-time generated <see cref="Task"/> call without reflection or <c>MakeGenericMethod</c>.</summary>
    public Task CallNone(MethodInfo method, object?[] args)
    {
        var (bound, call) = Prepare(method, args);
        return InvokeNone(call, bound.Shape, bound.TokenOf(args));
    }

    /// <summary>Executes a compile-time generated <see cref="Task{TResult}"/> call without reflection or <c>MakeGenericMethod</c>.</summary>
    public Task<TResult> CallTask<TResult>(MethodInfo method, object?[] args)
    {
        var (bound, call) = Prepare(method, args);
        return Typed<TResult>(call, bound.Shape, bound.TokenOf(args));
    }

    /// <summary>Executes a compile-time generated <see cref="ValueTask{TResult}"/> call without reflection or <c>MakeGenericMethod</c>.</summary>
    public ValueTask<TResult> CallValueTask<TResult>(MethodInfo method, object?[] args)
        => new(CallTask<TResult>(method, args));

    /// <summary>Executes a compile-time generated <see cref="IAsyncEnumerable{TItem}"/> stream call without reflection or <c>MakeGenericMethod</c>.</summary>
    public IAsyncEnumerable<TItem> CallStream<TItem>(MethodInfo method, object?[] args)
    {
        var (bound, call) = Prepare(method, args);
        return Items<TItem>(call, bound.TokenOf(args));
    }

    private (ProxyMethod Bound, RuntimeInvocation Call) Prepare(MethodInfo method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);
        var bound = Bind(method);
        return (bound, new RuntimeInvocation(string.Empty, _contract, _key, method.Name, bound.Encode(args)));
    }

    // Never cache collectible contracts in static dictionaries; doing so pins their AssemblyLoadContext.
    private static ProxyMethod Bind(MethodInfo method)
        => method.DeclaringType?.IsCollectible == true || method.ReflectedType?.IsCollectible == true
            ? new ProxyMethod(method)
            : Methods.GetOrAdd(method, static m => new ProxyMethod(m));

    private async Task InvokeNone(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
        => await Send(call, shape, cancellationToken).ConfigureAwait(false);

    // CA1859: these bind to one delegate shape, so they return object.
#pragma warning disable CA1859, CA2263
    private object? InvokeValue<TResult>(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
        => Typed<TResult>(call, shape, cancellationToken);

    private object? InvokeValueTask<TResult>(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
        => new ValueTask<TResult>(Typed<TResult>(call, shape, cancellationToken));

#pragma warning restore CA1859

    private async Task<TResult> Typed<TResult>(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
    {
        var value = await Send(call, shape, cancellationToken).ConfigureAwait(false);
        return value is { } json ? (TResult)RuntimeProtocol.Deserialize(json, typeof(TResult))! : default!;
    }

    private object? StreamItems<TItem>(RuntimeInvocation call, CallShape shape, CancellationToken cancellationToken)
    {
        _ = shape;
        return Items<TItem>(call, cancellationToken);
    }

    private async IAsyncEnumerable<TItem> Items<TItem>(RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _sink.Stream(Target, call, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return (TItem)RuntimeProtocol.Deserialize(item, typeof(TItem))!;
        }
    }

    private readonly record struct CallShape(TimeSpan? Timeout, string Method);
}
