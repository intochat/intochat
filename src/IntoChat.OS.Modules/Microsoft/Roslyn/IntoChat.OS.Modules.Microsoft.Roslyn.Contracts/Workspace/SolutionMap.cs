namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[GenerateSerializer]
[Alias("coding.solution-map")]
public sealed record SolutionMap(
    [property: Id(0)] string SolutionPath,
    [property: Id(1)] ProjectNode[] Projects,
    [property: Id(2)] ProjectEdge[] References);
