using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Modules.Apps;

// What each first-party shell app reaches outside its own namespace, granted when the brain holds it as a shipped
// copy from the os publisher. Targets are plain-neuron ids ({type}/{brain}/{key}); a person's own grants are untouched.
internal static class ShippedGrants
{
    public const string Publisher = "os";

    public static Grant[] For(string brain, string app) => app switch
    {
        "desktop" => Tuples(brain, app, [Workspace(brain), Apps(brain), Person(brain), Compute(brain),
            $"apps.installed/{brain}/*",
            $"chat/{IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), "ino").ChildKey("transcript")}", $"agent/{brain}/*", $"textfield/{brain}/settings/workspace/new-tab-app"]),
        ShippedApps.Settings => Tuples(brain, app, [Apps(brain), $"apps.store/{brain}/*", $"apps.installed-modules/{brain}/*", $"apps.sideload/{brain}/*",
            $"integrations.accounts/{brain}/*", $"ai.models/{brain}/*", $"chat-index/{brain}/*", $"textfield/{brain}/settings/*"]),
        var craft when ArtCraftCatalog.IsArtCraftApp(craft) => Tuples(
            brain,
            ArtCraftCatalog.NormalizeAppId(craft),
            [$"apps.artcraft-engine/{brain}/*", $"artcraft/{brain}/*", $"mcp/{brain}/*"]),
        _ => [],
    };

    private static string Workspace(string brain) => $"ui.workspace/{brain}/main";
    private static string Apps(string brain) => IntoChat.OS.Host.Primitives.AppRef.WildcardTarget(new DigitalBrain.BrainId(brain));
    private static string Person(string brain) => $"person/{brain}/profile";
    private static string Compute(string brain) => $"ui.compute/{brain}/compute";

    private static Grant[] Tuples(string brain, string app, string[] targets)
        => [.. targets.Select(target => Grant.Tuple($"brain:{brain}", $"app:{IntoChat.OS.Host.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), app).Key}", target, GrantRelations.Run))];
}
