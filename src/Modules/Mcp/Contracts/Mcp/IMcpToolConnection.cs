using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Tools;

namespace IntoChat.OS.Modules.Mcp;

[JsonConverter(typeof(JsonStringEnumConverter<McpConfirmationMode>))]
public enum McpConfirmationMode
{
    None = 0,
    OncePerSession = 1,
    EveryCall = 2,
}

public sealed record McpConnectionConfig(
    string Url,
    string? Title = null,
    string? HeaderName = null,
    string? HeaderPrefix = null,
    string[]? AllowedTools = null,
    McpConfirmationMode ConfirmationMode = McpConfirmationMode.None);

public sealed record McpServerState(
    string Title,
    string Url,
    bool Connected,
    ToolDescriptor[] Tools,
    string? Error,
    DateTimeOffset? RefreshedAt,
    string? HeaderName = null,
    string? HeaderPrefix = null,
    string[]? AllowedTools = null,
    McpConfirmationMode ConfirmationMode = McpConfirmationMode.None);

[Signal("mcp.tool-ran")]
public sealed record McpToolRan(string Tool, bool IsError, int ElapsedMs) : Signal;

[Signal("mcp.server-changed")]
public sealed record McpServerChanged(McpServerState State) : Signal;

// A connection neuron representing an external MCP tool source offering tool@1 capability.
// Keyed {brain}/connection/mcp/{name}; owns its optional sealed token and cached MCP client session.
[NeuronAlias(TypeName, Formerly = ["IntoChat.OS.Modules.Mcp.IMcpServer"]), DefaultNeuronType(TypeName)]
public interface IMcpToolConnection : ITool, IToolSource
{
    const string TypeName = "mcp";

    static string KeyOf(string name) => $"connection/{TypeName}/{name}";

    static NeuronId NeuronIdOf(BrainId brain, string name) => new(brain, TypeName, KeyOf(name));

    [CallTimeout(120_000)]
    Task<McpServerState> ConnectServer(McpConnectionConfig config, CancellationToken ct = default);

    [CallTimeout(120_000)]
    Task<McpServerState> Configure(McpConnectionConfig config, CancellationToken ct = default);

    Task DisconnectServer(CancellationToken ct = default);

    [ReadOnlyCall, Interleaves]
    Task<McpServerState> ReadState(CancellationToken ct = default);

    [ReadOnlyCall]
    Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default);

    [ReadOnlyCall]
    Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default);
}

public sealed record McpConnectionSeed(
    string Name,
    McpConnectionConfig Config,
    string? Token = null);

public interface IMcpConnectionSeedProvider
{
    ValueTask<McpConnectionSeed?> ResolveAsync(string ownerBrainId, CancellationToken ct);
}
