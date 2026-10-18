using IntoChat.OS.ArtCraft.Aspire.Hosting;

namespace IntoChat.OS.Kernel.Tests.Unit;

// e2e.yml warms the ArtCraft images with the same pinned commits the hosting builds; a drifted ref warms nothing.
public sealed class E2EWarmUpFacts
{
    [Fact]
    public void TheWorkflowWarmsTheArtCraftImagesAtTheHostedRefs()
    {
        var workflow = File.ReadAllText(Path.Combine(SolutionProjects.Root, ".github", "workflows", "e2e.yml"));

        foreach (var (app, reference) in new[] { ("photocraft", ArtCraftModuleHosting.PhotoCraftRef), ("filmcraft", ArtCraftModuleHosting.FilmCraftRef) })
        {
            foreach (var dockerfile in new[] { "craft-mcp.Dockerfile", "craft-web.Dockerfile" })
            {
                Assert.Contains(workflow.Split('\n'), line => line.Contains(dockerfile, StringComparison.Ordinal)
                    && line.Contains($"--build-arg APP={app} ", StringComparison.Ordinal)
                    && line.Contains($"--build-arg REF={reference} ", StringComparison.Ordinal));
            }
        }
    }
}
