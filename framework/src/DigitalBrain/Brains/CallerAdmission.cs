using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;

namespace DigitalBrain.Brains;

public static class CallerAdmission
{
    internal static ICallerAmbient? Callers { get; set; }

    internal static bool TryGetCaller([NotNullWhen(true)] out Caller? caller)
    {
        caller = Callers?.Current;
        return caller is not null;
    }

    public static void RequireSameBrain(string brainId, string? appId = null, string? target = null)
    {
        if (!TryGetCaller(out var caller)) { return; }
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
        if (TryGetCaller(out var caller) && IsApp(caller))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    private static bool IsApp(Caller caller)
        => caller.Actor is Actor.App || caller.Actor is Actor.Person person && AppPrincipal.IsApp(person.Principal);

    private static bool CanTouchBrain(Caller caller, string brainId, string? appId)
    {
        var sameBrain = string.Equals(caller.Brain.Value, brainId, StringComparison.Ordinal);
        return caller.Actor switch
        {
            Actor.Kernel => true,
            Actor.App app => appId is not null && sameBrain && string.Equals(app.Key, appId, StringComparison.Ordinal),
            Actor.Person or Actor.Agent => sameBrain,
            _ => false,
        };
    }
}
