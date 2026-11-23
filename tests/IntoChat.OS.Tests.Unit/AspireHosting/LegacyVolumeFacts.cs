using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LegacyVolumeFacts
{
    [Theory]
    [InlineData("local", "ai-ollama", "Modules-ollama")]
    [InlineData("cloud", "Modules-storage", "Modules-storage")]
    [InlineData("cloud", "Modules-postgres-server", "Modules-postgres-server")]
    [InlineData("cloud", "Modules-clickhouse-server", "Modules-clickhouse-server")]
    public void EveryDataVolumeKeepsTheNameItHadBeforeTheRename(string topology, string resourceName, string legacyName)
    {
        var os = AppHostChainFacts.Compose(topology);
        var builder = os.ApplicationBuilder;

        var resource = Assert.Single(builder.Resources, r => r.Name == resourceName);
        var mount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>(), static m => m.Type == ContainerMountType.Volume);

        var expected = VolumeNameGenerator.Generate(builder.CreateResourceBuilder(new ContainerResource(legacyName)), "data");
        Assert.Equal(expected, mount.Source);
        Assert.Equal(LegacyNames.DataVolume(builder, legacyName), mount.Source);
    }

    [Fact]
    public void AnExplicitAzuriteVolumeOverridesThePinnedName()
    {
        var os = AppHostChainFacts.Compose("cloud", ("IntoChatOS:Modules:orleans:Options:Azurite:DataVolume", "my-azurite-data"));

        var storage = Assert.Single(os.ApplicationBuilder.Resources, static r => r.Name == "Modules-storage");
        var mount = Assert.Single(storage.Annotations.OfType<ContainerMountAnnotation>(), static m => m.Type == ContainerMountType.Volume);

        Assert.Equal("my-azurite-data", mount.Source);
    }
}
