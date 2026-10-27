using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Operations;
using IntoChat.OS.Confirmation;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

public sealed class DriveConfirmationFacts(DriveHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Direct_delete_waits_for_human_confirmation_and_denial_preserves_the_file()
    {
        var storage = Assert.IsType<AsOwner>(await host.ConnectKitAsync(Ct, DriveScopes.Write, ConfirmationPolicy.DestructiveOnly));
        var name = host.KitDrive.Seed(DigitalBrain.Testing.Conformance.Storage.StorageSeed.Canonical);
        var file = await DriveItems.Find(storage, name + "/Plan.txt", Ct);
        var deleting = storage.Write(WritePlan.Delete(file.Id), Ct);
        var request = await Pending(storage.Owner, deleting);
        Assert.Equal(ConfirmationAction.Delete, request.Plan.Action);
        using (storage.Owner.AsCaller()) { await Inbox(storage.Owner).Resolve(request.PlanId, false); }
        await Assert.ThrowsAsync<Denied>(() => deleting);
        Assert.Equal(file.Id, (await storage.Stat(file.Id, Ct)).Id);
    }

    [Fact]
    public async Task Stream_upload_is_confirmed_once_before_accepting_bytes()
    {
        var storage = Assert.IsType<AsOwner>(await host.ConnectKitAsync(Ct, DriveScopes.Write, ConfirmationPolicy.Always));
        var root = Assert.Single(await storage.Roots(Ct));
        var name = "approved-" + Guid.NewGuid().ToString("N") + ".txt";
        var opening = storage.OpenWrite(WritePlan.WriteFile(root.Id, name), Ct);
        var request = await Pending(storage.Owner, opening);
        using (storage.Owner.AsCaller()) { await Inbox(storage.Owner).Resolve(request.PlanId, true); }
        await using var stream = await opening.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await stream.Content.WriteAsync("approved"u8.ToArray(), Ct);
        var id = await stream.Commit(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("approved", await storage.ReadText(id, Ct));
        using (storage.Owner.AsCaller()) { Assert.Empty((await Inbox(storage.Owner).Read()).Pending); }
    }

    private IConfirmationInbox Inbox(Person person) => host.KitBrain.Get<IConfirmationInbox>(IConfirmationInbox.Key(person.BrainId));

    private async Task<ConfirmationRequested> Pending(Person person, Task operation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var caller = person.AsCaller();
        while (true)
        {
            var pending = (await Inbox(person).Read()).Pending;
            if (pending.Length != 0) { return Assert.Single(pending); }
            Assert.False(operation.IsCompleted, "The provider must wait for the human confirmation.");
            await Task.Delay(20, timeout.Token);
        }
    }
}
