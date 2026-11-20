using DigitalBrain.Identity.Local.Machine;
using IntoChat.OS.Kernel.Composition.Machine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class MachineHostingFacts : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "intochat-machine-hosting-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void TheConfiguredFileKeepsTheHostsMachineId()
    {
        var file = Path.Combine(_root, "machine-id");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(MachineHosting.IdFileKey, file)]).Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddMachineId().BuildServiceProvider();

        var id = provider.GetRequiredService<MachineId>().Value;

        Assert.Equal(id, File.ReadAllText(file));
    }

    [Fact]
    public void WithoutASettingTheIdStaysAtTheOsDefaultPath()
    {
        var file = MachineHosting.IdFile(null);

        Assert.Equal("machine-id", Path.GetFileName(file));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntoChatOS", "machine-id"), file);
        }
    }
}
