using System.Text.Json.Serialization;

namespace IntoChat.OS.Cli;

internal sealed record DevAppFile(string Path, string Content);

internal sealed record DevAppPush(DevAppFile[] Files);

internal sealed record DevDiagnostic(string Id, string Message, string File, int Line, string? Fix);

internal sealed record DevAppResult(DevDiagnostic[] Diagnostics);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DevAppPush))]
[JsonSerializable(typeof(DevAppResult))]
internal partial class CliJsonContext : JsonSerializerContext;
