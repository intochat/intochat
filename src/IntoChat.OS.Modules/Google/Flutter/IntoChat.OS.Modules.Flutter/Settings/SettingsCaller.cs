using IntoChat.OS.Apps;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Flutter.Settings;

// The Settings app the OS ships, running in this brain: the only caller that may configure the controls its
// view shows, so no other app (or an edited Settings) can put a misleading label on a person's preference or entry.
internal static class SettingsCaller
{
    public const string App = "settings";

    public static async Task<bool> IsShipped(IIntoChatOS brain, string? scope)
    {
        if (scope is null || !CallerContextStamper.TryGet(out var caller) || caller.Kind != CallerKind.App
            || caller.StampedBy != TrustedEdge.AppProxy || caller.AppId != IApp.Key(scope, App)) { return false; }
        return ShippedApps.Is(await brain.Get<IApp>(caller.AppId).Read(), App);
    }
}
