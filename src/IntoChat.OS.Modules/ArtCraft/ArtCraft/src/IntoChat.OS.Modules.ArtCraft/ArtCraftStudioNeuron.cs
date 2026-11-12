using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.ArtCraft;

[Neuron("artcraft.studio")]
internal sealed class ArtCraftStudioNeuron(IOptions<ArtCraftModuleOptions> options) : DigitalBrain.Neurons.Neuron, IArtCraftStudio
{
    private string Brain => Id.Brain.Value;

    public async Task<ArtCraftSession> Open(string app, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var normalized = ArtCraftCatalog.NormalizeAppId(app);
        var (title, mcp, web) = options.Value.For(normalized);
        if (mcp is null) { throw new InvalidOperationException($"{title} has no MCP server in this deployment."); }
        var key = $"{Brain}/mcp/{normalized}";
        var server = Get<IMcpServer>(DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IMcpServer), key));
        // Opening again is cheap: an already connected server on the same URL keeps its tool list.
        if (await server.Read() is not { Connected: true } state || state.Url != mcp)
        { await server.Connect(new McpServerSpec(title, mcp), ct); }
        return new ArtCraftSession(normalized, title, key, web);
    }
}
