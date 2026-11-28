using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Ino;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InoCard))]
[JsonSerializable(typeof(InoEngineerRunSummary))]
[JsonSerializable(typeof(InoCreateSquad))]
[JsonSerializable(typeof(InoOpenSquadPlan))]
[JsonSerializable(typeof(InoAddSquadTask))]
[JsonSerializable(typeof(InoMoveSquadTask))]
[JsonSerializable(typeof(InoToggleSquadTask))]
[JsonSerializable(typeof(InoChatAboutChoice))]
internal sealed partial class InoJson : JsonSerializerContext;
