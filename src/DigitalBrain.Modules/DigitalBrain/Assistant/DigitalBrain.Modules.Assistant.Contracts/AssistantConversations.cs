using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Assistant;

public static class AssistantConversations
{
    public static string AssistantKey(string workspace) => workspace + "/applications/assistant";

    public static string Key(string workspace, string thread) =>
        "agent-conversation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { workspace, thread })))).ToLowerInvariant();
}
