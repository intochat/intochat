using DigitalBrain.Framework;

namespace DigitalBrain.Flame;

// A brain owns its derived space; app scenes also admit their owning app.
internal static class SceneAccess
{
    public static string BrainOf(string key) => IsSpace(key)
        ? key[..^"/space".Length] : key[..key.IndexOf("/apps/", StringComparison.Ordinal)];
    private static bool IsSpace(string key) => key.EndsWith("/space", StringComparison.Ordinal) && !key.Contains("/apps/", StringComparison.Ordinal);
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
            CallerKind.App => !IsSpace(key) && SameBrain(caller, key) && string.Equals(caller.AppId, AppOf(key), StringComparison.Ordinal),
            _ => false,
        };
        if (!admitted) { throw new UnauthorizedAccessException($"A {caller.Kind} caller of brain {caller.BrainId} may not touch scene {key}."); }
    }

    private static bool SameBrain(CallerContext caller, string key) => string.Equals(caller.BrainId, BrainOf(key), StringComparison.Ordinal);
}
