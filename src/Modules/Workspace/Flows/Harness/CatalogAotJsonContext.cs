using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed record RoslynBatchEditArg(
    [property: JsonPropertyName("kind"), Description("add_using | add_or_replace_member | remove_member | create_type_file")] string Kind,
    [property: JsonPropertyName("path"), Description("Repo-relative .cs path")] string Path,
    [property: JsonPropertyName("type_name")] string? TypeName = null,
    [property: JsonPropertyName("member_name")] string? MemberName = null,
    [property: JsonPropertyName("namespace_name")] string? NamespaceName = null,
    [property: JsonPropertyName("source"), Description("Complete C# member declaration or type body")] string? Source = null,
    [property: JsonPropertyName("type_kind")] string? TypeKind = null,
    [property: JsonPropertyName("base_types")] string? BaseTypes = null);

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
[JsonSerializable(typeof(RoslynBatchEditArg))]
[JsonSerializable(typeof(RoslynBatchEditArg[]))]
[JsonSerializable(typeof(RoslynBatchErrorPayload))]
[JsonSerializable(typeof(RoslynBatchResultPayload))]
[JsonSerializable(typeof(ToolContentPayload))]
[JsonSerializable(typeof(RoslynSymbolsPayload))]
[JsonSerializable(typeof(RoslynDiagnosticsPayload))]
[JsonSerializable(typeof(RoslynEditResultPayload))]
[JsonSerializable(typeof(FileActionPayload))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class CatalogAotJsonContext : JsonSerializerContext
{
    public static CatalogAotJsonContext Relaxed { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
