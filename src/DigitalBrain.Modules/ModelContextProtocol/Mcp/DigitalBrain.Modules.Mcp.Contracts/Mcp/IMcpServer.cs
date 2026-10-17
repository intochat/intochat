using DigitalBrain.Tools;
using Orleans.Concurrency;

namespace DigitalBrain.Mcp;

// One MCP server reached over Streamable HTTP, keyed {brain}/mcp/{name}. Connect lists its tools and
// registers them with the brain's tool sources; Run calls one. Credentials never live here.
[Alias("mcp.server"), Orleans.Metadata.DefaultGrainType("mcp.server")]
public interface IMcpServer : IToolSource
{
    [ResponseTimeout("00:02:00")] Task<McpServerState> Connect(McpServerSpec spec, CancellationToken ct = default);
    Task Disconnect(CancellationToken ct = default);
    [ReadOnly, AlwaysInterleave] Task<McpServerState> Read();
}

[GenerateSerializer, Alias("mcp.server-spec")]
public sealed record McpServerSpec([property: Id(0)] string Title, [property: Id(1)] string Url);

[GenerateSerializer, Alias("mcp.server-state")]
public sealed record McpServerState(
    [property: Id(0)] string Title,
    [property: Id(1)] string Url,
    [property: Id(2)] bool Connected,
    [property: Id(3)] ToolDescriptor[] Tools,
    [property: Id(4)] string? Error,
    [property: Id(5)] DateTimeOffset? RefreshedAt);

[GenerateSerializer, Alias("mcp.tool-ran")]
public sealed record McpToolRan([property: Id(0)] string Tool, [property: Id(1)] bool IsError, [property: Id(2)] int ElapsedMs) : Signal;
