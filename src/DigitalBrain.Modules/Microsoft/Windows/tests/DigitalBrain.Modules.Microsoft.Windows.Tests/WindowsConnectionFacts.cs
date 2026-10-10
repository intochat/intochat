using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests;

public sealed class WindowsConnectionFacts
{
    [Fact(Timeout = 60_000)]
    public async Task ThisMachineConnectsAndReportsItsFacts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, machineId, _) = await WindowsHost.StartAsync(ct);
        await using var _ = host;

        var view = await host.Get<IConnection>(WindowsHost.Machine(machineId)).Connect("device", machineId);
        var facts = await host.Get<IWindows>(WindowsHost.Machine(machineId)).Facts();

        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Equal(machineId, facts.MachineId);
        Assert.Equal(Environment.MachineName, facts.MachineName);
        Assert.True(facts.ProcessorCount > 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task AnotherMachinesConnectionIsNotServedHere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, _, _) = await WindowsHost.StartAsync(ct);
        await using var _ = host;
        var other = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => host.Get<IWindows>(WindowsHost.Machine(other)).Facts());
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => host.Get<IConnection>(WindowsHost.Machine(other)).Connect("device", other));
    }

    [Fact(Timeout = 60_000)]
    public async Task RootsAreAllowedAndRevoked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var (host, machineId, root) = await WindowsHost.StartAsync(ct);
        await using var _ = host;
        var machine = host.Get<IWindows>(WindowsHost.Machine(machineId));

        await machine.AllowRoot(root);
        await machine.AllowRoot(root.ToUpperInvariant());
        Assert.Equal([Path.GetFullPath(root)], await machine.Roots());

        await machine.RevokeRoot(root);
        Assert.Empty(await machine.Roots());
    }
}
