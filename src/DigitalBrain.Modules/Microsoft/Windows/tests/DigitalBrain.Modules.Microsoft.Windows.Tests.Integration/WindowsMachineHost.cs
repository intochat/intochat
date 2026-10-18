using DigitalBrain.Microsoft.Windows;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Microsoft.Windows.Tests.Integration.WindowsMachineHost))]

namespace DigitalBrain.Modules.Microsoft.Windows.Tests.Integration;

// The shared host composes the Windows module reading one known machine id; each fact connects that machine from a
// fresh person's brain (WindowsHost.ForPersonAsync). Facts that swap the custody, compose another storage module or
// call the owner's HTTP routes keep their own TestBrain ([Trait("host", "own")]).
public sealed class WindowsMachineHost : BrainHostFixture
{
    private readonly string _folder = Directory.CreateTempSubdirectory("windows-facts").FullName;

    public string MachineId { get; } = Guid.NewGuid().ToString("N");

    protected override TestBrain Configure(TestBrain brain)
    {
        var idFile = Path.Combine(_folder, "machine-id");
        File.WriteAllText(idFile, MachineId);
        return brain.WithModule<WindowsModule>().WithOptions<WindowsModule, WindowsOptions>(options => options.MachineIdFile = idFile);
    }
}
