namespace IntoChat.OS.Tools;

// InputSchema is the tool's JSON schema for Run's arguments object.
[GenerateSerializer, Alias("tools.descriptor")]
public sealed record ToolDescriptor(
    [property: Id(0)] string Name,
    [property: Id(1)] string Title,
    [property: Id(2)] string Description,
    [property: Id(3)] string InputSchema,
    [property: Id(4)] bool ReadOnly);

// What a source offers right now: its title and its tools; a source that is not set up offers none.
[GenerateSerializer, Alias("tools.set")]
public sealed record ToolSet(
    [property: Id(0)] string Title,
    [property: Id(1)] ToolDescriptor[] Tools);

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
