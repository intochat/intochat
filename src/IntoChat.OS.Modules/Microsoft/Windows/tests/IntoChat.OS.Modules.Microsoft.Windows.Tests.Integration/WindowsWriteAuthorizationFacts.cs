using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Confirmation;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

public sealed class WindowsWriteAuthorizationFacts(WindowsMachineHost shared)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectDestructiveCallsWaitForTheOwnerAndDenialPreservesTheFile(bool streamed)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PC storage runs on Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        var path = Path.Combine(host.Root, "keep.txt");
        await File.WriteAllTextAsync(path, "keep", ct);
        using var owner = host.Owner.AsCaller();
        var broker = host.Brain.Services.GetRequiredService<CapabilityBroker>();
        var storage = Assert.Single(await broker.Storages(new(host.Owner.BrainId), ct), item => item.ConnectionKey == host.Key).Capability;
        var folder = Assert.Single(await storage.Roots(ct)).Id;
        var item = Assert.Single((await storage.List(folder, new(), ct)).Items).Id;
        var inbox = host.Brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(host.Owner.BrainId));
        Task request = streamed ? storage.OpenWrite(WritePlan.WriteFile(folder, "keep.txt", conflict: ConflictPolicy.Overwrite), ct)
            : storage.Write(WritePlan.Delete(item), ct);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        while ((await inbox.Read()).Pending.Length == 0)
        {
            if (request.IsCompleted) { await request; Assert.Fail("The destructive call completed without confirmation."); }
            await Task.Delay(20, wait.Token);
        }
        Assert.False(request.IsCompleted);
        Assert.Equal("keep", await File.ReadAllTextAsync(path, ct));
        await inbox.Resolve(Assert.Single((await inbox.Read()).Pending).PlanId, false);
        await Assert.ThrowsAsync<Denied>(() => request);
        Assert.Equal("keep", await File.ReadAllTextAsync(path, ct));
        Assert.Single(Directory.EnumerateFileSystemEntries(host.Root));
    }
}
