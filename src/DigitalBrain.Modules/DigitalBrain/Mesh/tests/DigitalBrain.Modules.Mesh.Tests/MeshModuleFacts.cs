using DigitalBrain.Mesh;

namespace DigitalBrain.Modules.Mesh.Tests;

public sealed class MeshModuleFacts
{
    [Fact]
    public void DefaultsCodeWideForHomeNodes()
    {
        var options = new MeshOptions();
        options.Validate();
        Assert.Equal((8, 16), (options.DataShards, options.ParityShards));
    }

    [Fact]
    public void MoreThan255ShardsIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => new MeshOptions { DataShards = 200, ParityShards = 100 }.Validate());
}
