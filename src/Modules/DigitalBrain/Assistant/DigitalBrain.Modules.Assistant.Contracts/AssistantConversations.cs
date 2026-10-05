using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Assistant;

public static class AssistantConversations
{
    // The assistant neuron behind /agent and /conversations keeps the key the old window used,
    // so stored conversations and usage receipts stay addressable.
    public static string AssistantKey(string workspace) => workspace + "/applications/assistant";

    // Preserve the existing persisted conversation identity during the host-to-app migration.
    public static string Key(string workspace, string thread) =>
        "agent-conversation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { workspace, thread })))).ToLowerInvariant();
}
