using DigitalBrain.Brains;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flame;

// A brain owns its derived space; app scenes also admit their owning app.
internal static class SceneAccess
{
    public static string BrainOf(string key) => IsSpace(key)
        ? key[..^"/space".Length] : key[..key.IndexOf("/apps/", StringComparison.Ordinal)];
    private static bool IsSpace(string key) => key.EndsWith("/space", StringComparison.Ordinal) && !key.Contains("/apps/", StringComparison.Ordinal);
    public static string AppOf(string key) => key[..key.IndexOf("/scenes/", StringComparison.Ordinal)];

    public static void RequireWriter(string key) => CallerAdmission.RequireSameBrain(BrainOf(key), IsSpace(key) ? null : AppOf(key), $"scene {key}");

    public static void RequireReporter(string key) => CallerAdmission.RequireSameBrain(BrainOf(key), IsSpace(key) ? null : AppOf(key), $"scene {key}");
}
