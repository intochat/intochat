using DigitalBrain.Capabilities;

namespace IntoChat.OS.Framework.Modules;

public static class CallerAdmission
{
    public static void RequireSameBrain(string brainId, string? appId = null, string? target = null)
    {
        if (!CallerContextStamper.TryGet(out var caller)) { return; }
        var sameBrain = string.Equals(caller.BrainId, brainId, StringComparison.Ordinal);
        var admitted = caller.Kind switch
        {
            CallerKind.Platform => true,
            CallerKind.User or CallerKind.Agent => sameBrain,
            CallerKind.App => appId is not null && sameBrain && string.Equals(caller.AppId, appId, StringComparison.Ordinal),
            _ => false,
        };
        if (!admitted)
        {
            throw new UnauthorizedAccessException($"A {caller.Kind} caller of brain {caller.BrainId} may not touch {target ?? brainId}.");
        }
    }

    public static void RequirePerson(string message)
    {
        var caller = CallerContextStamper.TryGet(out var stamped) ? stamped : throw new UnauthorizedAccessException("A circle acts only on a stamped caller.");
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy || AppPrincipal.IsApp(caller.PrincipalId))
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    public static void RequireBrainMember(string brainId, string message)
    {
        CapabilityCaller caller;
        try { caller = CallerContextStamper.Require(); }
        catch (UntrustedCallerException error) { throw new UnauthorizedAccessException(error.Message, error); }
        if (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy || AppPrincipal.IsApp(caller.PrincipalId)
            || BrainScope.Create(caller.AccountId, caller.BrainId).Id != brainId)
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    public static void RequireNotAppCaller(string message)
    {
        if (CallerContextStamper.TryGet(out var caller)
            && (caller.Kind == CallerKind.App || caller.StampedBy == TrustedEdge.AppProxy || AppPrincipal.IsApp(caller.PrincipalId)))
        {
            throw new UnauthorizedAccessException(message);
        }
    }
}
