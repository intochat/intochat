using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

// How Orleans honours the neutral scheduling attributes on methods a grain interface inherits from a contract
// (clean-core 2.6 limit 2, settled in PR 4). Orleans reads [AlwaysInterleave], [ReadOnly], [OneWay] and
// [ResponseTimeout] only on the method it generates the invokable for, through a meta-attribute a neutral
// attribute cannot carry, and redeclaring the method changes its invokable id. So the mapping never touches
// the invokable:
// - [Interleaves], [ReadOnlyCall] and [InterleavesWhen] are decided grain-side by the class's [MayInterleave]
//   predicate, which reads the neutral attributes from the request's interface method.
// - [OneWayCall] and [CallTimeout] are applied caller-side by NeutralCallFilter.
public static class NeutralCallMapping
{
    private sealed record Markers(bool Interleaves, bool OneWay, TimeSpan? Timeout);

    private static readonly ConcurrentDictionary<MethodInfo, Markers> Methods = new();
    private static readonly ConcurrentDictionary<Type, Func<MethodInfo, IReadOnlyList<object?>, bool>?> Predicates = new();

    // The body of a grain class's [MayInterleave] predicate. A [ReadOnlyCall] interleaves with every turn, not only
    // with other read-only calls as Orleans' [ReadOnly] does: the predicate sees only the incoming request.
    public static bool MayInterleave(Type neuronClass, IInvokable request)
    {
        ArgumentNullException.ThrowIfNull(neuronClass);
        ArgumentNullException.ThrowIfNull(request);
        var method = request.GetMethod();
        if (method is not null && Of(method).Interleaves) { return true; }
        if (Predicates.GetOrAdd(neuronClass, PredicateOf) is not { } predicate) { return false; }
        var arguments = new object?[request.GetArgumentCount()];
        for (var i = 0; i < arguments.Length; i++) { arguments[i] = request.GetArgument(i); }
        return method is not null && predicate(method, arguments);
    }

    public static bool IsOneWay(MethodInfo method) => Of(method).OneWay;

    public static TimeSpan? TimeoutOf(MethodInfo method) => Of(method).Timeout;

    private static Markers Of(MethodInfo method) => Methods.GetOrAdd(method, static m => new(
        m.IsDefined(typeof(global::DigitalBrain.InterleavesAttribute), false) || m.IsDefined(typeof(global::DigitalBrain.ReadOnlyCallAttribute), false),
        m.IsDefined(typeof(global::DigitalBrain.OneWayCallAttribute), false),
        m.GetCustomAttribute<global::DigitalBrain.CallTimeoutAttribute>(false) is { } timeout ? timeout.Timeout : null));

    // [InterleavesWhen(name)] names a static bool name(MethodInfo method, IReadOnlyList<object?> arguments) on the class.
    private static Func<MethodInfo, IReadOnlyList<object?>, bool>? PredicateOf(Type neuronClass)
    {
        if (neuronClass.GetCustomAttribute<global::DigitalBrain.InterleavesWhenAttribute>(false) is not { } when) { return null; }
        var method = neuronClass.GetMethod(when.Predicate, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(MethodInfo), typeof(IReadOnlyList<object?>)])
            ?? throw new InvalidOperationException($"{neuronClass.FullName} names interleaving predicate {when.Predicate}, which is not a static bool {when.Predicate}(MethodInfo, IReadOnlyList<object?>).");
        return method.ReturnType == typeof(bool)
            ? method.CreateDelegate<Func<MethodInfo, IReadOnlyList<object?>, bool>>()
            : throw new InvalidOperationException($"{neuronClass.FullName}.{when.Predicate} must return bool.");
    }
}

// Caller-side half of the mapping. A [OneWayCall] is sent and not awaited: the caller resumes at once and a fault
// is logged, never thrown. The message keeps its invokable id and still carries a response, which nothing awaits.
// A [CallTimeout] bounds the caller's wait; Orleans' own response timeout still applies, so a longer timeout
// needs the messaging timeout raised to the largest declared one (the provider does this in PR 5).
public sealed class NeutralCallFilter(ILogger<NeutralCallFilter> logger) : IOutgoingGrainCallFilter
{
    public Task Invoke(IOutgoingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var method = context.InterfaceMethod;
        if (method is null) { return context.Invoke(); }
        if (NeutralCallMapping.IsOneWay(method))
        {
            _ = context.Invoke().ContinueWith(
                call => logger.LogWarning(call.Exception, "One-way call {Interface}.{Method} failed.", context.InterfaceName, context.MethodName),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // The caller's chain completes now with an empty response; the request is already on its way.
            context.Response = Response.Completed;
            return Task.CompletedTask;
        }
        return NeutralCallMapping.TimeoutOf(method) is { } timeout ? context.Invoke().WaitAsync(timeout) : context.Invoke();
    }
}
