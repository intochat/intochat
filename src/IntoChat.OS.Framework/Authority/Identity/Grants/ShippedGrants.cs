using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Authority.Identity.Grants;

public static class ShippedGrants
{
    public const string Publisher = "os";

    public static Grant[] ForAssistant(string brain)
    {
        var assistantKey = IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(brain), CapabilityCallers.AssistantApp).Key;
        return
        [
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", "*", "*"),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", "*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", "*", GrantRelations.View),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", "*"),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{assistantKey}", "*", GrantRelations.View),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"agent/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"chat/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"files.vfs/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"ui.workspace/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"person/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"textfield/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"apps.app2/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"apps.artcraft-engine/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"artcraft/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"mcp.server/{brain}/*", GrantRelations.Run),
            Grant.Tuple($"brain:{brain}", $"app:{CapabilityCallers.AssistantApp}", $"ui.compute/{brain}/*", GrantRelations.Run),
        ];
    }

    // The command-line host (IntoChat.OS.Cli) joins the gateway as the app "cli" for a person; it reads what runs, nothing else.
    public const string CliApp = "cli";

    public static Grant[] ForCli(string brain) =>
    [
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"runs/{brain}/*", GrantRelations.Run),
        Grant.Tuple($"brain:{brain}", $"app:{CliApp}", $"flows/{brain}/*", GrantRelations.Run),
    ];

    public static Grant[] For(string brain, string app) => app switch
    {
        CapabilityCallers.AssistantApp => ForAssistant(brain),
        _ => [],
    };

    // First-party callers whose grants ship with the OS rather than being stored: added when the brain stores none of its own.
    public static Grant[] WithImplicit(Grant[] grants, CallerKind kind, string? appId, string brain)
    {
        if (kind == CallerKind.Agent && !grants.Any(g => g.AppId == CapabilityCallers.AssistantApp)) { return [.. grants, .. ForAssistant(brain)]; }
        if (kind == CallerKind.App && appId == CliApp && !grants.Any(g => g.AppId == CliApp)) { return [.. grants, .. ForCli(brain)]; }
        return grants;
    }
}
