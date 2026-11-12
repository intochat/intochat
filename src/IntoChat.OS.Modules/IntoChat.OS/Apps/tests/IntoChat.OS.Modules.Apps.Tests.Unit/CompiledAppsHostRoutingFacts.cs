using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

public sealed class VfsNeuronContractFacts
{
    [Fact]
    public void VfsNeuronAndContractsAreDecoratedWithShareableAttribute()
    {
        Assert.True(typeof(IVfsNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));
        Assert.True(typeof(IFilesNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));

        var neuronAttr = typeof(VfsNeuron).GetCustomAttribute<NeuronAttribute>();
        Assert.NotNull(neuronAttr);
        Assert.Equal("files.vfs", neuronAttr!.Type);
    }
}
