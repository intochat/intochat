using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Conversations;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ConversationState))]
[JsonSerializable(typeof(ConversationView))]
[JsonSerializable(typeof(ConversationEntry))]
[JsonSerializable(typeof(ConversationChanged))]
[JsonSerializable(typeof(TextDelta))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;
