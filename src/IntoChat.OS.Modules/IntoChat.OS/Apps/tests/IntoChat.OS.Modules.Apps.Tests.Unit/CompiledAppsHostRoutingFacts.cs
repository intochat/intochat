using System.Reflection;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests;

public sealed class CompiledAppsHostRoutingFacts
{
    [Fact]
    public void CompiledAppsHostContainsOnlyKernelShellApps()
    {
        var names = CompiledAppsHost.HostedBy(
        [
            new(typeof(AppsModule).Assembly),
            new(typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly, [typeof(IntoChat.OS.Apps.Ino.InoApp), typeof(IntoChat.OS.Apps.Settings.SettingsApp)]),
        ]).Keys.ToList();
        Assert.Equal(2, names.Count);

        Assert.Contains("ino", names);
        Assert.Contains("settings", names);
        Assert.DoesNotContain("gallery", names);
        Assert.DoesNotContain("creator", names);
        Assert.DoesNotContain("post", names);
        Assert.DoesNotContain("photocraft", names);
        Assert.DoesNotContain("filmcraft", names);
    }

    [Fact]
    public void VfsNeuronAndContractsAreDecoratedWithShareableAttribute()
    {
        Assert.True(typeof(IVfsNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));
        Assert.True(typeof(IFilesNeuron).IsDefined(typeof(ShareableAttribute), inherit: false));

        var neuronAttr = typeof(VfsNeuron).GetCustomAttribute<DigitalBrain.NeuronAttribute>();
        Assert.NotNull(neuronAttr);
        Assert.Equal("files.vfs", neuronAttr!.Type);
    }
}
