using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using IntoChat.OS.Framework;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Authority.Identity.Grants;

public static class ShippedGrants
{
    public const string Publisher = "os";

    public static Grant[] ForAssistant(string brain)
    {
        var assistantKey = IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), CallerApps.Assistant).Key;
        return
        [
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", "*", "*"),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", "*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", "*", GrantRelations.View),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", "*"),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", GrantRelations.View),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"agent/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"chat/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"files.vfs/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"ui.workspace/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"person/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"textfield/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"apps.app2/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"apps.artcraft-engine/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"artcraft/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"mcp.server/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CallerApps.Assistant}", $"ui.compute/{brain}/*", GrantRelations.Run),
        ];
    }

    // The command-line host (IntoChat.OS.Cli) joins the gateway as the app "cli" for a person; it reads what runs, sends app commands and starts workflows; answering approvals is not its to do.
    public const string CliApp = "cli";

    public static Grant[] ForCli(string brain) =>
    [
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"runs/{brain}/*", GrantRelations.Run),
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"flows/{brain}/*", GrantRelations.Run),
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"app.run/{brain}/*", GrantRelations.Run),
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"workflow.run/{brain}/*", GrantRelations.Run),
    ];

    public static Grant[] For(string brain, string app) => app switch
    {
        CallerApps.Assistant => ForAssistant(brain),
        _ => [],
    };

    // First-party callers whose grants ship with the OS rather than being stored: added when the brain stores none of its own.
    public static Grant[] WithImplicit(Grant[] grants, Caller caller, string brain)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var appId = caller.Actor is Actor.App app ? app.Key : null;
        if (caller.Actor is Actor.Agent && !grants.Any(g => g.AppId == CallerApps.Assistant)) { return [.. grants, .. ForAssistant(brain)]; }
        if (appId == CliApp && !grants.Any(g => g.AppId == CliApp)) { return [.. grants, .. ForCli(brain)]; }
        return grants;
    }
}
