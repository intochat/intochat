using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Files;

// A behavior's record. Wanted says it should be loaded in the brain's session; Owner is the caller who
// started it, whom the runtime edge speaks for as an app.
[GenerateSerializer, Alias("microsoft.csharp.file-state")]
internal sealed record CSharpFileState
{
    [Id(0)] public string Program { get; init; } = "";
    [Id(1)] public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
    [Id(2)] public string? Hash { get; init; }
    [Id(3)] public bool Wanted { get; init; }
    [Id(4)] public CallerContext? Owner { get; init; }
    [Id(5)] public string? AppId { get; init; }
    [Id(6)] public string? AppBrain { get; init; }
}
