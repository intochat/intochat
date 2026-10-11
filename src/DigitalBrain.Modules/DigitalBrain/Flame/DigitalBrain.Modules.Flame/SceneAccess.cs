using DigitalBrain.Framework;

namespace DigitalBrain.Flame;

// A scene is its app's: only that app, or a person/platform caller of the same brain, writes it; the shell
// (a User caller of the brain) reports gestures and fires events; nobody outside the brain touches it.
internal static class SceneAccess
{
    public static string BrainOf(string key) => key[..key.IndexOf("/apps/", StringComparison.Ordinal)];
    public static string AppOf(string key) => key[..key.IndexOf("/scenes/", StringComparison.Ordinal)];

    public static void RequireWriter(string key) => RequireBrainAndOwningApp(key);

    public static void RequireReporter(string key) => RequireBrainAndOwningApp(key);

    private static void RequireBrainAndOwningApp(string key)
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind == CallerKind.Platform) { return; }
        if (!string.Equals(caller.BrainId, BrainOf(key), StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("This scene belongs to another brain."); }
        if (caller.Kind == CallerKind.App && !string.Equals(caller.AppId, AppOf(key), StringComparison.Ordinal))
        { throw new UnauthorizedAccessException($"Scene {key} belongs to {AppOf(key)}."); }
    }
}
