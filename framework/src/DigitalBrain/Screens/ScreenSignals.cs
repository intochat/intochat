using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain.Neurons;

namespace DigitalBrain.Kernel;

/// <summary>A compile-time signal handler binding for a screen: its concrete signal type and direct-call dispatcher.</summary>
public sealed record ScreenSignalHandler(Type SignalType, Func<object, Signal, CancellationToken, Task> Dispatch);

/// <summary>The signals a screen class reacts to, mapped by journal alias.</summary>
public static class ScreenSignals
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> ByAlias = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, ScreenSignalHandler>> Registered = new();
    private static readonly ConditionalWeakTable<Type, IReadOnlyDictionary<string, ScreenSignalHandler>> CollectibleRegistered = new();

    /// <summary>Registers compile-time generated signal handlers for <paramref name="screenType"/>.</summary>
    public static void Register(Type screenType, IReadOnlyDictionary<string, ScreenSignalHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(screenType);
        ArgumentNullException.ThrowIfNull(handlers);

        if (screenType.IsCollectible)
        {
            CollectibleRegistered.AddOrUpdate(screenType, handlers);
            return;
        }

        Registered[screenType] = handlers;
        ByAlias[screenType] = BuildAliasMap(screenType, handlers);
    }

    /// <summary>Returns the compile-time registered handler for <paramref name="alias"/> on <paramref name="screen"/>, or <c>null</c> if none.</summary>
    public static ScreenSignalHandler? FindHandler(Type screen, string alias)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(alias);

        return TryGetRegistered(screen, out var handlers)
            ? handlers.GetValueOrDefault(alias)
            : null;
    }

    /// <summary>Returns all signal types handled by <paramref name="screen"/>, mapped by alias.</summary>
    public static IReadOnlyDictionary<string, Type> For(Type screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        if (screen.IsCollectible)
        {
            return CollectibleRegistered.TryGetValue(screen, out var collectibleHandlers)
                ? BuildAliasMap(screen, collectibleHandlers)
                : Scan(screen);
        }

        return ByAlias.GetOrAdd(screen, static type =>
            Registered.TryGetValue(type, out var handlers)
                ? BuildAliasMap(type, handlers)
                : Scan(type));
    }

    /// <summary>Finds the Signal type matching <paramref name="alias"/> for <paramref name="screen"/>.</summary>
    public static Type? Find(Type screen, string alias)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(alias);
        return For(screen).GetValueOrDefault(alias);
    }

    private static bool TryGetRegistered(Type screen, out IReadOnlyDictionary<string, ScreenSignalHandler> handlers)
    {
        if (screen.IsCollectible)
        {
            return CollectibleRegistered.TryGetValue(screen, out handlers!);
        }

        return Registered.TryGetValue(screen, out handlers!);
    }

    private static Dictionary<string, Type> BuildAliasMap(Type screen, IReadOnlyDictionary<string, ScreenSignalHandler> handlers)
    {
        var signals = new Dictionary<string, Type>(handlers.Count, StringComparer.Ordinal);
        foreach (var (alias, handler) in handlers)
        {
            signals[alias] = handler.SignalType;
        }

        // If the screen inherits through an intermediate base class (such as an OS screen base in another assembly),
        // merge any non-public [On] signals declared on that base chain that were invisible to the source generator.
        if (screen.BaseType is { } baseType && !IsDirectScreenBase(baseType))
        {
            foreach (var (alias, signalType) in Scan(baseType))
            {
                signals.TryAdd(alias, signalType);
            }
        }

        return signals;
    }

    private static bool IsDirectScreenBase(Type baseType)
        => baseType == typeof(object)
            || baseType == typeof(Neuron)
            || (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(Screen<>));

    private static Dictionary<string, Type> Scan(Type screen)
    {
        if (TryGetRegistered(screen, out var registered))
        {
            return BuildAliasMap(screen, registered);
        }

        var signals = new Dictionary<string, Type>(StringComparer.Ordinal);
        for (var type = screen; type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsDefined(typeof(OnAttribute), false) && method.GetParameters() is [{ ParameterType: var signal }, ..]
                    && typeof(Signal).IsAssignableFrom(signal) && !signal.IsAbstract)
                {
                    signals.TryAdd(SignalAliases.Of(signal), signal);
                }
            }
        }

        return signals;
    }
}
