using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Modules.Apps;

// What each first-party shell app reaches outside its own namespace, granted when the brain holds it as a shipped
// copy from the os publisher. Targets are plain-neuron ids ({type}/{brain}/{key}); a person's own grants are untouched.
internal static class ShippedGrants
{
    public const string Publisher = "os";

    public static Grant[] For(string brain, string app) => app switch
    {
        "workspace" => Tuples(brain, app, [Workspace(brain), Apps(brain), Person(brain), Compute(brain), $"text/{brain}/apps/desktop/layout"]),
        "desktop" => Tuples(brain, app, [Workspace(brain), Apps(brain), Person(brain), Compute(brain),
            $"chat/{brain}/apps/ino/transcript", $"agent/{brain}/*", $"textfield/{brain}/settings/workspace/new-tab-app"]),
        "settings" => Tuples(brain, app, [Apps(brain), $"apps.store/{brain}/*", $"apps.installed-modules/{brain}/*", $"apps.sideload/{brain}/*",
            $"integrations.accounts/{brain}/*", $"ai.models/{brain}/*", $"chat-index/{brain}/*", $"textfield/{brain}/settings/*"]),
        "data" => Tuples(brain, app, [$"files.vfs/{brain}/*"]),
        "markdraw" => Tuples(brain, app, [$"files.vfs/{brain}/*"]),
        var craft when ArtCraftCatalog.IsArtCraftApp(craft) => Tuples(
            brain,
            ArtCraftCatalog.NormalizeAppId(craft),
            [$"apps.artcraft-engine/{brain}/*", $"artcraft/{brain}/*", $"mcp.server/{brain}/*", $"files.vfs/{brain}/*"]),
        _ => [],
    };

    private static string Workspace(string brain) => $"ui.workspace/{brain}/main";
    private static string Apps(string brain) => $"{IntoChat.OS.Connections.AppGrainType.Name}/{brain}/apps/*";
    private static string Person(string brain) => $"person/{brain}/profile";
    private static string Compute(string brain) => $"ui.compute/{brain}/compute";

    private static Grant[] Tuples(string brain, string app, string[] targets)
        => [.. targets.Select(target => Grant.Tuple($"brain:{brain}", $"app:{brain}/apps/{app}", target, GrantRelations.Run))];
}
