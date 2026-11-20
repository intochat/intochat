using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Apps.Tasks;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PlanInner))]
internal sealed partial class TasksAppJsonContext : JsonSerializerContext;
