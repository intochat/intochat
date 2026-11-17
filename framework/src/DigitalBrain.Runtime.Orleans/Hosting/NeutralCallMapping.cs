using System.Reflection;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;

namespace DigitalBrain.Runtime.Orleans.Hosting;

/// <summary>The neutral scheduling attributes an Orleans call filter applies: one-way and timeout, read by <see cref="CallPolicy"/>.</summary>
public static class NeutralCallMapping
{
    /// <summary>Whether <paramref name="method"/> carries <see cref="OneWayCallAttribute"/>.</summary>
    public static bool IsOneWay(MethodInfo method) => CallPolicy.IsOneWay(method);

    /// <summary>The declared <see cref="CallTimeoutAttribute"/> timeout on <paramref name="method"/>, or null.</summary>
    public static TimeSpan? TimeoutOf(MethodInfo method) => CallPolicy.TimeoutOf(method);
}
