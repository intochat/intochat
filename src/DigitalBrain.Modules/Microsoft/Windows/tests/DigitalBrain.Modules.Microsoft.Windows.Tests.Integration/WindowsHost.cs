using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests.Integration;

// A person on the shared brain whose Windows module reads a known machine id, plus an empty folder to allow as a root.
internal sealed record WindowsHost(ITestBrain Brain, string MachineId, string Root, Person Owner)
{
    // A fresh person on the shared host, with an empty folder of their own to allow as a root.
    public static async Task<WindowsHost> ForPersonAsync(WindowsMachineHost shared, bool connect, bool allowRoot, CancellationToken ct)
    {
        var root = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("windows-facts").FullName, "root")).FullName;
        var host = new WindowsHost(shared.Brain, shared.MachineId, root, await shared.PersonAsync(ct: ct));
        using var caller = host.Owner.AsCaller();
        if (connect) { await host.Brain.Get<IConnection>(host.Key).Connect("device", host.MachineId); }
        if (allowRoot) { await host.Brain.Get<IWindows>(host.Key).AllowRoot(root); }
        return host;
    }

    public string Machine(string machineId) => ConnectionKeys.For(Owner.BrainId, "windows", machineId);

    public string Key => Machine(MachineId);
}
