using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class LegacyVolumeFacts
{
    [Theory]
    [InlineData("local", "Modules-ollama")]
    [InlineData("cloud", "Modules-storage")]
    [InlineData("cloud", "Modules-postgres-server")]
    [InlineData("cloud", "Modules-clickhouse-server")]
    public void EveryDataVolumeKeepsTheNameItHadBeforeTheRename(string topology, string legacyName)
    {
        var os = AppHostChainFacts.Compose(topology);
        var builder = os.ApplicationBuilder;

        var resource = Assert.Single(builder.Resources, r => r.Name == legacyName);
        var mount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>(), static m => m.Type == ContainerMountType.Volume);

        var expected = VolumeNameGenerator.Generate(builder.CreateResourceBuilder(new ContainerResource(legacyName)), "data");
        Assert.Equal(expected, mount.Source);
        Assert.Equal(LegacyNames.DataVolume(builder, legacyName), mount.Source);
    }
}
