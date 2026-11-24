using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.ArtCraft;

[Neuron("artcraft.studio")]
internal sealed partial class ArtCraftStudioNeuron(IOptions<ArtCraftModuleOptions> options) : Neuron, IArtCraftStudio
{
    [Slot(Required = false)]
    private partial SlotCollection<IMcpToolConnection> McpServers { get; }

    private string Brain => Id.Brain.Value;

    public async Task<ArtCraftSession> Open(string app, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var normalized = ArtCraftCatalog.NormalizeAppId(app);
        var (title, mcp, web) = options.Value.For(normalized);
        if (mcp is null) { throw new InvalidOperationException($"{title} has no MCP server in this deployment."); }
        var localKey = IMcpToolConnection.KeyOf(normalized);
        var key = $"{Brain}/{localKey}";
        var server = McpServers[localKey];
        // Opening again is cheap: an already connected server on the same URL keeps its tool list.
        if (await server.ReadState(ct) is not { Connected: true } state || state.Url != mcp)
        { await server.ConnectServer(new McpConnectionConfig(mcp, title), ct); }
        return new ArtCraftSession(normalized, title, key, web);
    }
}
