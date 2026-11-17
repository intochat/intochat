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

    public static Grant[] For(string brain, string app) => app switch
    {
        CapabilityCallers.AssistantApp => ForAssistant(brain),
        _ => [],
    };
}
