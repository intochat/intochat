using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Orleans.Concurrency;

namespace DigitalBrain.Runtime.InMemory;

// The scheduling a contract method declares, read from the neutral attributes and, until contracts migrate,
// the Orleans ones they replace. A class-level [Reentrant] interleaves every call; a neutral [InterleavesWhen]
// predicate decides per call. A class with Orleans' [MayInterleave] is refused at registration.
internal sealed record CallMarkers(bool Interleaves, bool OneWay, TimeSpan? Timeout)
{
    private static readonly ConcurrentDictionary<MethodInfo, CallMarkers> Methods = new();

    public static CallMarkers Of(Type neuronClass, MethodInfo method, object?[] arguments)
    {
        var declared = Methods.GetOrAdd(method, Read);
        if (declared.Interleaves) { return declared; }
        if (neuronClass.IsDefined(typeof(ReentrantAttribute), false)) { return declared with { Interleaves = true }; }
        if (neuronClass.GetCustomAttribute<InterleavesWhenAttribute>(false) is { } when
            && neuronClass.GetMethod(when.Predicate, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(MethodInfo), typeof(IReadOnlyList<object?>)]) is { } predicate
            && predicate.Invoke(null, [method, arguments]) is true)
        { return declared with { Interleaves = true }; }
        return declared;
    }

    private static CallMarkers Read(MethodInfo method)
    {
        var interleaves = method.IsDefined(typeof(InterleavesAttribute), false) || method.IsDefined(typeof(ReadOnlyCallAttribute), false)
            || method.IsDefined(typeof(AlwaysInterleaveAttribute), false) || method.IsDefined(typeof(ReadOnlyAttribute), false);
        var oneWay = method.IsDefined(typeof(OneWayCallAttribute), false) || method.IsDefined(typeof(OneWayAttribute), false);
        TimeSpan? timeout = method.GetCustomAttribute<CallTimeoutAttribute>(false) is { } neutral
            ? TimeSpan.Parse(neutral.Timeout, CultureInfo.InvariantCulture)
            : method.GetCustomAttribute<ResponseTimeoutAttribute>(false)?.Timeout;
        return new(interleaves, oneWay, timeout);
    }
}
