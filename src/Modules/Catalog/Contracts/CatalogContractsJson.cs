using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Apps;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProfileState))]
[JsonSerializable(typeof(ProfilePostItem))]
[JsonSerializable(typeof(Audience))]
public sealed partial class CatalogContractsJson : JsonSerializerContext;
