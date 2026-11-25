using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

public sealed class FilesContractFacts
{
    [Fact]
    public void The_files_contracts_do_not_opt_in_to_calls_from_other_brains()
    {
        // [Shareable] is what lets a Shared or Public operation be called from another brain. Files are shared through
        // IFileShares; the files contracts themselves stay closed.
        Assert.False(typeof(IFiles).IsDefined(typeof(ShareableAttribute), inherit: false));
        Assert.True(typeof(IFileShares).IsDefined(typeof(ShareableAttribute), inherit: false));
        // files.vfs is gone: no neuron of this assembly is hosted under that type.
        Assert.DoesNotContain(typeof(FileSharesDefaults).Assembly.GetTypes().Concat(typeof(IntoChat.OS.Modules.Apps.AppsModule).Assembly.GetTypes()),
            type => type.GetCustomAttribute<NeuronAttribute>()?.Type == "files.vfs");
    }
}
