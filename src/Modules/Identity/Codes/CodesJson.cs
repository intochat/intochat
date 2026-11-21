using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Identity.Contracts.Codes;

namespace IntoChat.OS.Modules.Identity.Codes;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CodeIndexRecord))]
[JsonSerializable(typeof(CodeKeyRecord))]
[JsonSerializable(typeof(CodeTarget))]
internal sealed partial class CodesJson : JsonSerializerContext;
