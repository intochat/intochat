using DigitalBrain.Framework;

namespace DigitalBrain.Flame;

// A scene is its app's. Admitted, for writing and for reporting/firing alike: the platform, a User or Assistant
// of the scene's brain, and the owning app of that brain. Every other kind (Scheduler) is refused.
internal static class SceneAccess
{
    public static string BrainOf(string key) => key[..key.IndexOf("/apps/", StringComparison.Ordinal)];
    public static string AppOf(string key) => key[..key.IndexOf("/scenes/", StringComparison.Ordinal)];

    public static void RequireWriter(string key) => RequireBrainAndOwningApp(key);

    public static void RequireReporter(string key) => RequireBrainAndOwningApp(key);

    private static void RequireBrainAndOwningApp(string key)
    {
        // An unstamped call is an in-silo call no edge made, admitted as in every other module.
        if (!CallerContextStamper.TryGet(out var caller)) { return; }
        var admitted = caller.Kind switch
        {
            CallerKind.Platform => true,
            CallerKind.User or CallerKind.Assistant => SameBrain(caller, key),
            CallerKind.App => SameBrain(caller, key) && string.Equals(caller.AppId, AppOf(key), StringComparison.Ordinal),
            _ => false,
        };
        if (!admitted) { throw new UnauthorizedAccessException($"A {caller.Kind} caller of brain {caller.BrainId} may not touch scene {key}."); }
    }

    private static bool SameBrain(CallerContext caller, string key) => string.Equals(caller.BrainId, BrainOf(key), StringComparison.Ordinal);
}
