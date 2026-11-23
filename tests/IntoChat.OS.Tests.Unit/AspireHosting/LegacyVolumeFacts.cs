using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LegacyVolumeFacts
{
    // The tails are literals read off the volumes the resources had before the rename (docker volume ls), so a
    // wrong suffix in the pin fails here instead of mounting a new, empty volume.
    [Theory]
    [InlineData("local", "ai-ollama", "-Modules-ollama-ollama")]
    [InlineData("cloud", "Modules-storage", "-Modules-storage-data")]
    [InlineData("cloud", "Modules-postgres-server", "-Modules-postgres-server-data")]
    [InlineData("cloud", "Modules-clickhouse-server", "-Modules-clickhouse-server-data")]
    public void EveryDataVolumeKeepsTheNameItHadBeforeTheRename(string topology, string resourceName, string expectedTail)
    {
        var os = AppHostChainFacts.Compose(topology);
        var builder = os.ApplicationBuilder;

        var source = VolumeSource(builder, resourceName);

        Assert.EndsWith(expectedTail, source, StringComparison.Ordinal);
        Assert.StartsWith(AppHostPrefix(builder), source, StringComparison.Ordinal);
        Assert.Equal(AppHostPrefix(builder).Length + expectedTail.Length - 1, source.Length);
    }

    [Fact]
    public void TheOllamaVolumeIsNotPinnedWithTheDefaultSuffix()
    {
        var builder = AppHostChainFacts.Compose("local").ApplicationBuilder;
        Assert.False(VolumeSource(builder, "ai-ollama").EndsWith("-data", StringComparison.Ordinal));
    }

    // The suffix each integration's WithDataVolume() gives its default name; the pins above copy these.
    [Fact]
    public void TheIntegrationsNameTheirDefaultVolumesWithTheseSuffixes()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddOllama("o").WithDataVolume();
        builder.AddPostgres("p").WithDataVolume();
        builder.AddClickHouse("c").WithDataVolume();
        builder.AddAzureStorage("s").RunAsEmulator(emulator => emulator.WithDataVolume());

        Assert.Equal(AppHostPrefix(builder) + "o-ollama", VolumeSource(builder, "o"));
        Assert.Equal(AppHostPrefix(builder) + "p-data", VolumeSource(builder, "p"));
        Assert.Equal(AppHostPrefix(builder) + "c-data", VolumeSource(builder, "c"));
        Assert.Equal(AppHostPrefix(builder) + "s-data", VolumeSource(builder, "s"));
    }

    [Fact]
    public void AnExplicitAzuriteVolumeOverridesThePinnedName()
    {
        var os = AppHostChainFacts.Compose("cloud", ("IntoChatOS:Modules:orleans:Options:Azurite:DataVolume", "my-azurite-data"));

        Assert.Equal("my-azurite-data", VolumeSource(os.ApplicationBuilder, "Modules-storage"));
    }

    private static string VolumeSource(IDistributedApplicationBuilder builder, string resourceName)
    {
        var resource = Assert.Single(builder.Resources, r => r.Name == resourceName);
        var mount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>(), static m => m.Type == ContainerMountType.Volume);
        return Assert.IsType<string>(mount.Source);
    }

    // "<app>-<hash>-": what Aspire puts before "<resource>-<suffix>".
    private static string AppHostPrefix(IDistributedApplicationBuilder builder)
    {
        var probe = VolumeNameGenerator.Generate(builder.CreateResourceBuilder(new ContainerResource("probe")), "x");
        return probe[..^"probe-x".Length];
    }
}
