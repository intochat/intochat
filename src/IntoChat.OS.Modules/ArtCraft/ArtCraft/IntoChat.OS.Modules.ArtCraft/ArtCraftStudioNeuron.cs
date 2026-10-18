using IntoChat.OS.Framework;
using IntoChat.OS.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.ArtCraft;

[GrainType("artcraft.studio")]
internal sealed class ArtCraftStudioNeuron(IOptions<ArtCraftModuleOptions> options) : Neuron, IArtCraftStudio
{
    private string Brain => BrainKey.BrainOf(this.GetPrimaryKeyString());

    public async Task<ArtCraftSession> Open(string app, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        var (title, mcp, web) = options.Value.For(app);
        if (mcp is null) { throw new InvalidOperationException($"{title} has no MCP server in this deployment."); }
        var key = $"{Brain}/mcp/{app}";
        var server = ServiceProvider.GetRequiredService<IIntoChatOSBrains>().Open(Brain).Neuron<IMcpServer>($"mcp/{app}");
        // Opening again is cheap: an already connected server on the same URL keeps its tool list.
        if (await server.Read() is not { Connected: true } state || state.Url != mcp)
        { await server.Connect(new McpServerSpec(title, mcp), ct); }
        return new ArtCraftSession(app, title, key, web);
    }
}
