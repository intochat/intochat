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

internal sealed record ToolContentPayload(bool Ok, string Content);

internal sealed record RoslynSymbolsPayload(bool Ok, int Count, IReadOnlyList<WorktreeSymbolInfo> Symbols);

internal sealed record RoslynDiagnosticsPayload(bool Ok, int Count, IReadOnlyList<WorktreeDiagnostic> Diagnostics);

internal sealed record RoslynEditResultPayload(
    bool Ok,
    IReadOnlyList<string> TouchedFiles,
    IReadOnlyList<WorktreeDiagnostic> Diagnostics,
    string? Diff,
    string? Detail);

internal sealed record FileActionPayload(bool Ok, string Path, string Action);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RoslynBatchErrorPayload))]
[JsonSerializable(typeof(RoslynBatchResultPayload))]
[JsonSerializable(typeof(ToolContentPayload))]
[JsonSerializable(typeof(RoslynSymbolsPayload))]
[JsonSerializable(typeof(RoslynDiagnosticsPayload))]
[JsonSerializable(typeof(RoslynEditResultPayload))]
[JsonSerializable(typeof(FileActionPayload))]
internal sealed partial class CatalogAotJsonContext : JsonSerializerContext;
