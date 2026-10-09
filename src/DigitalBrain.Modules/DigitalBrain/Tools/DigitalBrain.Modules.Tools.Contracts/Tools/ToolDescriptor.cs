namespace DigitalBrain.Tools;

// InputSchema is the tool's JSON schema for Run's arguments object.
[GenerateSerializer, Alias("tools.descriptor")]
public sealed record ToolDescriptor(
    [property: Id(0)] string Name,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string InputSchema,
    [property: Id(4)] bool ReadOnly);

// Contract is the full name of the source's own neuron contract (e.g. DigitalBrain.Mcp.IMcpServer).
[GenerateSerializer, Alias("tools.source-entry")]
public sealed record ToolSourceEntry(
    [property: Id(0)] string Key,
    [property: Id(1)] string Contract,
    [property: Id(2)] string Title,
    [property: Id(3)] ToolDescriptor[] Tools);

// Blobs carry binary content (a rendered PNG): base64 Data, or a Uri when the source stored it.
[GenerateSerializer, Alias("tools.result")]
public sealed record ToolResult(
    [property: Id(0)] bool IsError,
    [property: Id(1)] string Text,
    [property: Id(2)] ToolBlob[] Blobs);

[GenerateSerializer, Alias("tools.blob")]
public sealed record ToolBlob(
    [property: Id(0)] string MediaType,
    [property: Id(1)] string Data,
    [property: Id(2)] string? Uri = null);
