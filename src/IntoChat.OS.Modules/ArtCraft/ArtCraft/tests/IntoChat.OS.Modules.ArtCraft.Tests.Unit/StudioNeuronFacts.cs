using DigitalBrain;
using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.ArtCraft.Tests;

// The plain studio neuron (06-06a). The Unit tier runs each fact on TestOS.
// Opening a served app is proven against a live MCP server in ArtCraftStudioFacts.
public sealed class StudioNeuronFacts(ArtCraftFixture fixture) : IClassFixture<ArtCraftFixture>
{
    private static readonly BrainId FactsBrain = new("facts");

    [Fact]
    public async Task AnAppThatIsNotArtCraftIsRefused()
    {
        var runtime = fixture.Runtime;
        var studio = runtime.Get<IArtCraftStudio>(new NeuronId(FactsBrain, "artcraft.studio", "artcraft"));

        await Assert.ThrowsAsync<ArgumentException>(() => studio.Open("paint", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAppWithoutAnMcpServerInThisDeploymentIsRefused()
    {
        var runtime = fixture.Runtime;
        var studio = runtime.Get<IArtCraftStudio>(new NeuronId(FactsBrain, "artcraft.studio", "artcraft"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => studio.Open("photocraft", TestContext.Current.CancellationToken));
    }
}
