using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record RoslynBatchErrorPayload(bool Ok, string Error);

internal sealed record RoslynBatchResultPayload(
    bool Ok,
    IReadOnlyList<string> TouchedFiles,
    IReadOnlyList<WorktreeDiagnostic> Diagnostics,
    string? Detail);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RoslynBatchErrorPayload))]
[JsonSerializable(typeof(RoslynBatchResultPayload))]
internal sealed partial class CatalogAotJsonContext : JsonSerializerContext;
