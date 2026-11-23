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
    private partial SlotCollection<IMcpServer> McpServers { get; }

    private string Brain => Id.Brain.Value;

    public async Task<ArtCraftSession> Open(string app, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var normalized = ArtCraftCatalog.NormalizeAppId(app);
        var (title, mcp, web) = options.Value.For(normalized);
        if (mcp is null) { throw new InvalidOperationException($"{title} has no MCP server in this deployment."); }
        var key = $"{Brain}/mcp/{normalized}";
        var server = McpServers[$"mcp/{normalized}"];
        // Opening again is cheap: an already connected server on the same URL keeps its tool list.
        if (await server.Read() is not { Connected: true } state || state.Url != mcp)
        { await server.Connect(new McpServerSpec(title, mcp), ct); }
        return new ArtCraftSession(normalized, title, key, web);
    }
}
