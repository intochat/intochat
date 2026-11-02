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
        var hostedField = typeof(CompiledAppsHost).GetField("Hosted", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(hostedField);
        var hosted = (Array)hostedField!.GetValue(null)!;
        Assert.NotNull(hosted);
        Assert.Equal(2, hosted.Length);

        var names = new List<string>();
        for (var i = 0; i < hosted.Length; i++)
        {
            var item = hosted.GetValue(i);
            var nameField = item!.GetType().GetField("Item1")!;
            names.Add((string)nameField.GetValue(item)!);
        }

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
