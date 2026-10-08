using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Files;

// A file's record: its program, its settings and the caller who started it. The runtime speaks for that
// caller as the owner of the file's one-behavior program.
[GenerateSerializer, Alias("microsoft.csharp.file-state")]
internal sealed record CSharpFileState
{
    [Id(0)] public string Program { get; init; } = "";
    [Id(1)] public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
    [Id(2)] public string? Hash { get; init; }
    [Id(3)] public bool Wanted { get; init; }
    [Id(4)] public CallerContext? Owner { get; init; }
}
