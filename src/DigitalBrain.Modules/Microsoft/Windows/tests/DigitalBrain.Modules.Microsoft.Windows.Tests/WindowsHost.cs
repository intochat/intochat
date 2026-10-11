using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

// A brain whose Windows module reads a known machine id, plus an empty folder to allow as a root.
internal sealed record WindowsHost(ITestBrain Brain, string MachineId, string Root)
{
    public static async Task<WindowsHost> StartAsync(bool connect, bool allowRoot, CancellationToken ct)
    {
        var temp = Directory.CreateTempSubdirectory("windows-facts").FullName;
        var idFile = Path.Combine(temp, "machine-id");
        var machineId = Guid.NewGuid().ToString("N");
        File.WriteAllText(idFile, machineId);
        var root = Directory.CreateDirectory(Path.Combine(temp, "root")).FullName;
        var brain = await TestBrain.Create()
            .WithModule<WindowsModule>()
            .WithOptions<WindowsModule, WindowsOptions>(options => options.MachineIdFile = idFile)
            .StartAsync(ct);
        var host = new WindowsHost(brain, machineId, root);
        using var caller = brain.Owner.AsCaller();
        if (connect) { await brain.Get<IConnection>(host.Key).Connect("device", machineId); }
        if (allowRoot) { await brain.Get<IWindows>(host.Key).AllowRoot(root); }
        return host;
    }

    public string Machine(string machineId) => ConnectionKeys.For(Brain.Owner.BrainId, "windows", machineId);

    public string Key => Machine(MachineId);
}
