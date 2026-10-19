using IntoChat.OS.ArtCraft.Aspire.Hosting;

namespace IntoChat.OS.Kernel.Tests.Unit;

// artcraft-images.yml builds the ArtCraft images at the same pinned commits the hosting names, and e2e.yml pulls the
// ref12 tags of those refs; a REF bump without a matching image makes e2e pull a tag nobody published.
public sealed class E2EWarmUpFacts
{
    [Fact]
    public void TheWorkflowWarmsTheArtCraftImagesAtTheHostedRefs()
    {
        var warm = File.ReadAllText(Path.Combine(SolutionProjects.Root, ".github", "workflows", "artcraft-images.yml"));
        var e2e = File.ReadAllText(Path.Combine(SolutionProjects.Root, ".github", "workflows", "e2e.yml"));
        Assert.Contains("docker build -f \"${DOCKERFILE}\"", warm, StringComparison.Ordinal);
        Assert.Contains("--build-arg APP=\"${APP}\" --build-arg REF=\"${REF}\"", warm, StringComparison.Ordinal);

        foreach (var (app, reference) in new[] { ("photocraft", ArtCraftModuleHosting.PhotoCraftRef), ("filmcraft", ArtCraftModuleHosting.FilmCraftRef) })
        {
            foreach (var kind in new[] { "mcp", "web" })
            {
                Assert.Contains(warm.Split('\n'), line => line.Contains($"app: {app},", StringComparison.Ordinal)
                    && line.Contains($"kind: {kind},", StringComparison.Ordinal)
                    && line.Contains($"ref: {reference},", StringComparison.Ordinal)
                    && line.Contains($"craft-{kind}.Dockerfile", StringComparison.Ordinal));
                Assert.Contains($"ghcr.io/intochat/os-artcraft-{app}-{kind}:{reference[..12]}", e2e, StringComparison.Ordinal);
            }
        }
    }
}
