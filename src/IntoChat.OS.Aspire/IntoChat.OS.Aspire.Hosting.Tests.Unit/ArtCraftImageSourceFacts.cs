using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.ArtCraft.Aspire.Hosting;
using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

// A configured registry makes ArtCraft run the prebuilt GHCR images (e2e); unset keeps the local Dockerfile build.
public sealed class ArtCraftImageSourceFacts
{
    private static readonly (string App, string Ref)[] Apps =
        [("photocraft", ArtCraftModuleHosting.PhotoCraftRef), ("filmcraft", ArtCraftModuleHosting.FilmCraftRef)];

    private static IDistributedApplicationBuilder Compose(string? registry)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithHosting<ArtCraftModuleHosting, ArtCraftModuleOptions>(o => { o.WithHostedApps(); o.ImageRegistry = registry; });
        return builder;
    }

    private static IResource Resource(IDistributedApplicationBuilder builder, string app, string kind) =>
        Assert.Single(builder.Resources, r => r.Name.EndsWith(app + "-" + kind, StringComparison.Ordinal));

    [Fact]
    public void AConfiguredRegistryRunsTheTaggedImagesWithoutADockerfileBuild()
    {
        var builder = Compose("ghcr.io/intochat");
        foreach (var (app, reference) in Apps)
        {
            foreach (var kind in new[] { "mcp", "web" })
            {
                var image = Assert.Single(Resource(builder, app, kind).Annotations.OfType<ContainerImageAnnotation>());
                Assert.Equal($"ghcr.io/intochat/os-artcraft-{app}-{kind}", image.Image);
                Assert.Equal(reference[..12], image.Tag);
                Assert.Empty(Resource(builder, app, kind).Annotations.OfType<DockerfileBuildAnnotation>());
            }
        }
    }

    [Fact]
    public void WithoutARegistryTheDockerfilesAreBuiltLocally()
    {
        var builder = Compose(null);
        foreach (var (app, _) in Apps)
        {
            foreach (var kind in new[] { "mcp", "web" })
            {
                Assert.Single(Resource(builder, app, kind).Annotations.OfType<DockerfileBuildAnnotation>());
            }
        }
    }
}
