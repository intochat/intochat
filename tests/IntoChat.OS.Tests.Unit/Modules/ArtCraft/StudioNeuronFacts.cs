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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var runtime = fixture.Runtime;
        var studio = runtime.Get<IArtCraftStudio>(new NeuronId(FactsBrain, "artcraft.studio", "artcraft"));

        await Assert.ThrowsAsync<ArgumentException>(() => studio.Open("paint", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAppWithoutAnMcpServerInThisDeploymentIsRefused()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", new BrainId("facts")));
        var runtime = fixture.Runtime;
        var studio = runtime.Get<IArtCraftStudio>(new NeuronId(FactsBrain, "artcraft.studio", "artcraft"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => studio.Open("photocraft", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CatalogRegistersCompleteGetArtCraftSuiteWithUniqueIdsAndPorts()
    {
        Assert.True(ArtCraftCatalog.All.Count >= 16);
        var ids = ArtCraftCatalog.All.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(ArtCraftCatalog.All.Count, ids.Count);

        var webPorts = ArtCraftCatalog.All.Select(a => a.DefaultWebPort).ToHashSet();
        var mcpPorts = ArtCraftCatalog.All.Select(a => a.DefaultMcpPort).ToHashSet();
        Assert.Equal(ArtCraftCatalog.All.Count, webPorts.Count);
        Assert.Equal(ArtCraftCatalog.All.Count, mcpPorts.Count);

        foreach (var expected in new[]
        {
            "artcraft", "photocraft", "filmcraft", "vectorcraft", "lightcraft", "effectcraft",
            "designcraft", "pdfcraft", "wordcraft", "soundcraft", "deckcraft", "cadcraft",
            "gridcraft", "figcraft", "texturecraft", "materialcraft", "solidcraft"
        })
        {
            Assert.True(ArtCraftCatalog.TryGet(expected, out var descriptor), $"Missing {expected} in ArtCraftCatalog.");
            Assert.NotEmpty(descriptor.Title);
            Assert.NotEmpty(descriptor.CliBinary);
            Assert.StartsWith("#", descriptor.AccentHex, StringComparison.Ordinal);
        }
    }
}
