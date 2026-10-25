using System.Reflection;
using System.Text.Json;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client;

// A behavior's brain.Get<T>(id): every call on the contract becomes one invocation in the behavior's name.
// DispatchProxy needs a public, non-sealed type with a public constructor.
#pragma warning disable CA1852
public class NeuronProxy : DispatchProxy
#pragma warning restore CA1852
{
    private static readonly MethodInfo InvokeTypedMethod = typeof(NeuronProxy).GetMethod(nameof(InvokeTypedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private SessionConnection _session = null!;
    private string _behavior = "";
    private BehaviorTrace _trace = new();

    internal Type Contract { get; private set; } = null!;

    internal string Key { get; private set; } = "";

    internal static T Create<T>(SessionConnection session, string behavior, string key, BehaviorTrace trace)
        => (T)Create(typeof(T), session, behavior, key, trace);

    internal static object Create(Type contract, SessionConnection session, string behavior, string key, BehaviorTrace trace)
    {
        var proxy = Create(contract, typeof(NeuronProxy));
        var neuron = (NeuronProxy)proxy;
        neuron._session = session;
        neuron._behavior = behavior;
        neuron._trace = trace;
        neuron.Contract = contract;
        neuron.Key = key;
        return proxy;
    }

    // A neuron name is unique within its brain, so two proxies for one name are one neuron (a set member).
    public override bool Equals(object? obj) => obj is NeuronProxy other && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Key);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var parameters = targetMethod.GetParameters();
        var cancellationToken = CancellationToken.None;
        List<JsonElement> arguments = [];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].ParameterType == typeof(CancellationToken))
            {
                cancellationToken = args?[index] is CancellationToken token ? token : CancellationToken.None;
                continue;
            }
            arguments.Add(JsonSerializer.SerializeToElement(args?[index], parameters[index].ParameterType, RuntimeProtocol.Json));
        }
        var invocation = new RuntimeInvocation(_behavior, Contract.FullName!, Key, targetMethod.Name, [.. arguments]);
        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(Task)) { return _session.InvokeAsync(invocation, _trace.TraceParent, cancellationToken); }
        if (returnType == typeof(ValueTask)) { return new ValueTask(_session.InvokeAsync(invocation, _trace.TraceParent, cancellationToken)); }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            return InvokeTypedMethod.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(this, [invocation, cancellationToken]);
        }
        throw new NotSupportedException($"{Contract.Name}.{targetMethod.Name} returns {returnType.Name}; scripts can call methods returning Task or Task<T>.");
    }

    private async Task<TResult> InvokeTypedAsync<TResult>(RuntimeInvocation invocation, CancellationToken cancellationToken)
    {
        var value = await _session.InvokeAsync(invocation, _trace.TraceParent, cancellationToken).ConfigureAwait(false);
        return value is { } json ? json.Deserialize<TResult>(RuntimeProtocol.Json)! : default!;
    }
}
