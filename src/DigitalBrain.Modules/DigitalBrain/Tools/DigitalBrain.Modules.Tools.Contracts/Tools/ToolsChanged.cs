namespace DigitalBrain.Tools;

// Published by IToolSources when a source registers, refreshes or leaves.
[GenerateSerializer, Alias("tools.changed")]
public sealed record ToolsChanged([property: Id(0)] string Key, [property: Id(1)] int Count) : Signal;
