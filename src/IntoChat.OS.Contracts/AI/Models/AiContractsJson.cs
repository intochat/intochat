using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Models;

[JsonSerializable(typeof(ModelCard))]
[JsonSerializable(typeof(ModelCard[]))]
[JsonSerializable(typeof(RoleMap))]
[JsonSerializable(typeof(ModelId))]
public sealed partial class AiContractsJson : JsonSerializerContext;
