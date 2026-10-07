using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DigitalBrain.Assistant;

public static class AssistantConversations
{
    public static string AssistantKey(string workspace) => workspace + "/applications/assistant";

    // A conversation is an agent in the workspace's brain; its key starts with that brain, as every agent key does.
    public static string Key(string workspace, string thread) =>
        workspace + "/agents/conversation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { workspace, thread })))).ToLowerInvariant();
}
