using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InoCard))]
internal sealed partial class InoJson : JsonSerializerContext;
