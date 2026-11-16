using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

public sealed class VfsNeuronContractFacts
{
    [Fact]
    public void The_files_contracts_do_not_opt_in_to_calls_from_other_brains()
    {
        // [Shareable] is what lets a Shared or Public operation be called from another brain. Files are shared through
        // IFileShares; the files contracts themselves stay closed.
        Assert.False(typeof(IVfsNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));
        Assert.False(typeof(IFilesNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));
        Assert.True(typeof(IFileShares).IsDefined(typeof(ShareableAttribute), inherit: false));

        var neuronAttr = typeof(VfsNeuron).GetCustomAttribute<NeuronAttribute>();
        Assert.NotNull(neuronAttr);
        Assert.Equal("files.vfs", neuronAttr!.Type);
    }
}
