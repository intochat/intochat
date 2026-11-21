using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.State;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class SharedWithMeRecord
{
    public SharedPointer[] Pointers { get; set; } = [];
}

// A person's side of the shares others made for them, at "{brain}/files/shared-with-me". The edge admits a person only
// to their own brain, so their requests go through here to the owner's door. A pointer is a convenience: when the door
// refuses, the pointer goes.
[Neuron(SharedWithMeDefaults.TypeName)]
internal sealed class SharedWithMeNeuron(IServiceProvider services) : Neuron<SharedWithMeRecord>, ISharedWithMe
{
    private IBrainOwners Owners => services.GetRequiredService<IBrainOwners>();

    public async Task<SharedPointer> Add(string ownerBrain, string shareId)
    {
        await Person();
        var share = await Door(ownerBrain).Describe(shareId);
        var owner = await Owners.OwnerAccount(new DigitalBrain.BrainId(ownerBrain)) ?? string.Empty;
        var pointer = new SharedPointer(ownerBrain, owner, share.Id, share.Path, share.IsFolder, share.Role);
        await Store(new SharedWithMeRecord { Pointers = [.. Others(ownerBrain, shareId), pointer] });
        return pointer;
    }

    public async Task<SharedPointer[]> Refresh()
    {
        await Person();
        var friends = await Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value)).List();
        var pointers = new List<SharedPointer>();
        foreach (var friend in friends)
        {
            // A friend whose door refuses shares nothing; one that cannot be reached keeps the pointers it had.
            try
            {
                var shares = await Door(friend.Brain.Value).SharedWithCaller();
                pointers.AddRange(shares.Select(share => new SharedPointer(friend.Brain.Value, friend.Account.Value, share.Id, share.Path, share.IsFolder, share.Role)));
            }
            catch (UnauthorizedAccessException refused) { OsConsoleLog.For<SharedWithMeNeuron>().LogSwallowed(refused, "a friend's door shares nothing with this brain"); }
            catch (Exception error) when (error is TimeoutException or IOException)
            {
                pointers.AddRange(State.Pointers.Where(pointer => pointer.OwnerBrain == friend.Brain.Value));
            }
        }

        await Store(new SharedWithMeRecord { Pointers = [.. pointers] });
        return State.Pointers;
    }

    public async Task Forget(string ownerBrain, string shareId)
    {
        await Person();
        await Drop(ownerBrain, shareId);
    }

    public async Task<SharedPointer[]> Pointers()
    {
        await Person();
        return State.Pointers;
    }

    public Task<SharedEntry[]> List(string ownerBrain, string shareId, string? relativePath)
        => Through(ownerBrain, shareId, door => door.List(shareId, relativePath));

    public Task<byte[]?> Read(string ownerBrain, string shareId, string? relativePath)
        => Through(ownerBrain, shareId, door => door.Read(shareId, relativePath));

    public Task<SharedEntry> Write(string ownerBrain, string shareId, string relativePath, byte[] content)
        => Through(ownerBrain, shareId, door => door.Write(shareId, relativePath, content));

    private async Task<T> Through<T>(string ownerBrain, string shareId, Func<IFileShares, Task<T>> call)
    {
        await Person();
        try { return await call(Door(ownerBrain)); }
        catch (UnauthorizedAccessException)
        {
            await Drop(ownerBrain, shareId);
            throw;
        }
    }

    // The owner of this brain, as a person.
    private async Task Person()
    {
        var (_, brain) = await PersonActs.Require(Context.Caller, Owners);
        if (brain != Id.Brain) { throw PersonActs.Refused(); }
    }

    private IFileShares Door(string ownerBrain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerBrain);
        if (string.Equals(ownerBrain, Id.Brain.Value, StringComparison.Ordinal)) { throw new ArgumentException("That is your own brain.", nameof(ownerBrain)); }
        return SiloNeurons.Get<IFileShares>(services, FileSharesDefaults.Id(ownerBrain));
    }

    private IEnumerable<SharedPointer> Others(string ownerBrain, string shareId)
        => State.Pointers.Where(pointer => pointer.OwnerBrain != ownerBrain || pointer.ShareId != shareId);

    private async Task Drop(string ownerBrain, string shareId)
    {
        var kept = Others(ownerBrain, shareId).ToArray();
        if (kept.Length != State.Pointers.Length) { await Store(new SharedWithMeRecord { Pointers = kept }); }
    }
}
