namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[GenerateSerializer]
[Alias("coding.map-query")]
public sealed record MapQuery([property: Id(0)] bool IncludeDocumentCounts = true);
