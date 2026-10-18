using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Microsoft.Windows;

namespace DigitalBrain.Modules.Microsoft.Windows.Tests.Integration;

public sealed class WindowsConnectionFacts(WindowsMachineHost shared)
{
    [Fact]
    public async Task ThisMachineConnectsAndReportsItsFacts()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();

        var view = await brain.Get<IConnection>(host.Key).Connect("device", host.MachineId);
        var facts = await brain.Get<IWindows>(host.Key).Facts();

        Assert.Equal(ConnectionStatus.Connected, view.Status);
        Assert.Equal(host.MachineId, facts.MachineId);
        Assert.Equal(Environment.MachineName, facts.MachineName);
        Assert.True(facts.ProcessorCount > 0);
    }

    [Fact]
    public async Task AnotherMachinesConnectionIsNotServedHere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var other = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => brain.Get<IWindows>(host.Machine(other)).Facts());
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => brain.Get<IConnection>(host.Machine(other)).Connect("device", other));
    }

    [Fact]
    public async Task RootsAreAllowedAndRevoked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: false, allowRoot: false, ct);
        var brain = host.Brain;
        using var caller = host.Owner.AsCaller();
        var machine = brain.Get<IWindows>(host.Key);

        await machine.AllowRoot(host.Root);
        await machine.AllowRoot(host.Root.ToUpperInvariant());
        Assert.Equal([Path.GetFullPath(host.Root)], await machine.Roots());

        await machine.RevokeRoot(host.Root);
        Assert.Empty(await machine.Roots());
    }
}
