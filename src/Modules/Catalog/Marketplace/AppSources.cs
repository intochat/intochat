using DigitalBrain.Brains;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps;

internal static class AppSources
{
    // The brain scope a source names, or null for a source that is not a brain.
    public static string? Scope(this AppSource source)
        => source is AppSource.Brain brain ? BrainScope.Create(brain.AccountId, brain.BrainId).Id : null;
}
