using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;

namespace DigitalBrain.Brains;

public static class CallerAdmission
{
    private static readonly Lock RuntimesLock = new();
    private static ICallerAmbient[] s_runtimes = [];

    // An explicit ambient that replaces the registered runtimes (facts set and restore it).
    internal static ICallerAmbient? Callers { get; set; }

    // Each runtime in the process registers its ambient once: a test process hosts TestOS and an Orleans silo side by side, and
    // a call is stamped on at most one of them, so the first one carrying a caller answers.
    internal static void Register(ICallerAmbient ambient)
    {
        ArgumentNullException.ThrowIfNull(ambient);
        lock (RuntimesLock)
        {
            if (!s_runtimes.Contains(ambient)) { s_runtimes = [.. s_runtimes, ambient]; }
        }
    }

    internal static bool TryGetCaller([NotNullWhen(true)] out Caller? caller)
    {
        caller = Callers is { } explicitAmbient
            ? explicitAmbient.Current
            : Array.Find(Volatile.Read(ref s_runtimes), ambient => ambient.Current is not null)?.Current;
        return caller is not null;
    }

    public static void RequireSameBrain(string brainId, string? appId = null, string? target = null)
    {
        var caller = Require();
        if (!CanTouchBrain(caller, brainId, appId))
        {
            throw new UnauthorizedAccessException($"A {caller.Actor.GetType().Name} caller of brain {caller.Brain} may not touch {target ?? brainId}.");
        }
    }

    public static void RequirePerson(string message)
    {
        var caller = TryGetCaller(out var stamped) ? stamped : throw new UnauthorizedAccessException("A circle acts only on a stamped caller.");
        if (IsApp(caller))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    public static void RequireBrainMember(string brainId, string message)
    {
        if (!TryGetCaller(out var caller))
        {
            throw new UnauthorizedAccessException("No caller context was stamped at a trusted edge.");
        }
        if (IsApp(caller) || caller.Brain.Value != brainId)
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    public static void RequireNotAppCaller(string message)
    {
        if (IsApp(Require()))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    private static Caller Require()
        => TryGetCaller(out var caller) ? caller : throw new UnauthorizedAccessException("No caller context was stamped at a trusted edge.");

    private static bool IsApp(Caller caller) => caller.Actor is Actor.App;

    private static bool CanTouchBrain(Caller caller, string brainId, string? appId)
    {
        var sameBrain = string.Equals(caller.Brain.Value, brainId, StringComparison.Ordinal);
        return caller.Actor switch
        {
            Actor.Kernel => true,
            Actor.App app => appId is not null && sameBrain && string.Equals(app.Key, appId, StringComparison.Ordinal),
            Actor.Person person => sameBrain && !AppPrincipal.IsApp(person.Principal),
            Actor.Agent => sameBrain,
            _ => false,
        };
    }
}
