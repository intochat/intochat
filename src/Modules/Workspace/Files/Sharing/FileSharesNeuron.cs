using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Components;
using DigitalBrain.State;
using IntoChat.OS.Modules.Identity.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class FileSharesRecord
{
    public WorkspaceShare[] Shares { get; set; } = [];
    // The last save made through a share, per canonical path: who and when.
    public FileShareSave[] Saves { get; set; } = [];
}

internal sealed record FileShareSave(string Path, string Account, DateTimeOffset At);

// The door of a brain's shared files, at "{brain}/files/shares". Its own list is the only authority: a removed share is
// refused on the next call. A visitor is served only while the roster lists them as a friend: the door asks on every
// visitor call, and drops a person's shares when the roster states that they were removed. It reads and writes the
// workspace by exact path, as the brain's files module.
[Neuron(FileSharesDefaults.TypeName)]
internal sealed class FileSharesNeuron(IServiceProvider services) : Neuron<FileSharesRecord>, IFileShares
{
    private const int KeptSaves = 500;

    private IBrainOwners Owners => services.GetRequiredService<IBrainOwners>();
    private TimeProvider Clock => Context.Time;
    private IOffers Offers => Context.Offers;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken);
        // Only the roster's own fact is heard: a signal of the same name from another neuron never arrives here.
        await Context.Wires.Ensure<FriendRemoved>(FriendsDefaults.Id(Id.Brain.Value));
        if (State.Shares.Length == 0) { return; }

        // Shares for a person removed while this door could not hear it (it was written before the fact existed) go now,
        // so they cannot come back with a later friendship.
        var gone = new List<string>();
        foreach (var account in State.Shares.Select(static s => s.Account).Distinct(StringComparer.Ordinal))
        {
            if (!await Friends.IsFriend(new AccountId(account))) { gone.Add(account); }
        }
        foreach (var account in gone) { await Remove(account); }

        foreach (var group in State.Shares.GroupBy(static s => s.Account, StringComparer.Ordinal))
        {
            var desired = group.Any(static s => s.Role == ShareRole.Editor) ? Role.Editor : Role.Viewer;
            try
            {
                if (await Offers.RoleOf(Id, group.Key, cancellationToken) != desired)
                {
                    await Offers.Offer(Id, group.Key, desired, cancellationToken);
                }
            }
            catch (UnauthorizedAccessException error)
            {
                // Persisted share belongs to an ex-friend; leave without an offer.
                GetLogger().LogSwallowed(error, "file share offer refused");
            }
        }
    }

    public async Task<WorkspaceShare> Share(string account, string path, ShareRole role)
    {
        var owner = await Owner();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (!Enum.IsDefined(role)) { throw new ArgumentException("The role is Viewer or Editor.", nameof(role)); }
        if (string.Equals(account, owner, StringComparison.Ordinal)) { throw new ArgumentException("A share names another person.", nameof(account)); }

        if (!await Friends.IsFriend(new AccountId(account))) { throw new UnauthorizedAccessException("Share only with a confirmed friend."); }
        var canonical = SharePath.Normalize(path);
        var item = await Files(files => files.Find(canonical))
            ?? throw new ArgumentException("There is no file or folder at that path.", nameof(path));

        var share = new WorkspaceShare(Guid.NewGuid().ToString("N"), account, canonical, item.Kind == ItemKind.Folder, role, Clock.GetUtcNow());
        var kept = State.Shares.Where(existing => existing.Account != account || existing.Path != canonical);
        WorkspaceShare[] nextShares = [.. kept, share];
        await Commit(nextShares, State.Saves);
        await SyncOfferForAccountAsync(account, nextShares);
        return share;
    }

    public async Task Unshare(string shareId)
    {
        await Owner();
        var removed = State.Shares.FirstOrDefault(share => share.Id == shareId);
        if (removed is null) { return; }
        WorkspaceShare[] nextShares = [.. State.Shares.Where(share => share.Id != shareId)];
        await Commit(nextShares, State.Saves);
        await SyncOfferForAccountAsync(removed.Account, nextShares);
    }

    public async Task UnshareAccount(string account)
    {
        await Owner();
        await Remove(account);
    }

    // Kept for one release (OWED.md): a node that still runs the earlier roster calls it. The roster of this version publishes FriendRemoved.
    public async Task DropAccount(string account)
    {
        if (Context.Caller?.Via is not { Type: FriendsDefaults.TypeName } via || via.Brain != Id.Brain) { throw PersonActs.Refused(); }
        await Remove(account);
    }

    // The roster removed a friend. Their shares go; until this turn runs, Admit refuses them.
    [On]
    public Task On(FriendRemoved removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        return Remove(removed.Account.Value);
    }

    private async Task Remove(string account)
    {
        if (State.Shares.All(share => share.Account != account)) { return; }
        await Commit([.. State.Shares.Where(share => share.Account != account)], State.Saves);
        await Offers.Revoke(Id, account);
    }

    private Task SyncOfferForAccountAsync(string account, WorkspaceShare[] shares)
    {
        var remaining = shares.Where(s => string.Equals(s.Account, account, StringComparison.Ordinal)).ToArray();
        if (remaining.Length == 0)
        {
            return Offers.Revoke(Id, account);
        }

        var highest = remaining.Any(static s => s.Role == ShareRole.Editor) ? Role.Editor : Role.Viewer;
        return Offers.Offer(Id, account, highest);
    }

    private IFriends Friends => Get<IFriends>(FriendsDefaults.Id(Id.Brain.Value));

    public async Task<WorkspaceShare[]> Shares()
    {
        await Owner();
        return await Files(async files =>
        {
            var shares = new WorkspaceShare[State.Shares.Length];
            for (var i = 0; i < State.Shares.Length; i++)
            {
                var share = State.Shares[i];
                var item = await files.Find(share.Path);
                var isFolder = item?.Kind == ItemKind.Folder;
                shares[i] = share with { Broken = item is null || isFolder != share.IsFolder };
            }
            return shares;
        });
    }

    public async Task<WorkspaceShare[]> SharedWithCaller()
    {
        var (account, _) = await FriendRelay.Require(Context.Caller, SharedWithMeDefaults.TypeName, Owners);
        if (!await Friends.IsFriend(new AccountId(account))) { throw PersonActs.Refused(); }
        return [.. State.Shares.Where(share => share.Account == account)];
    }

    public async Task<WorkspaceShare> Describe(string shareId) => (await Admit(shareId)).Share;

    public async Task<SharedEntry[]> List(string shareId, string? relativePath)
    {
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        var entries = await Files<IReadOnlyList<(string Path, Item Item)>>(async files =>
        {
            if (await files.Find(path) is not { } item) { return []; }
            return item.Kind == ItemKind.Folder ? [.. (await files.List(path)).Select(child => (path + "/" + child.Name, child))] : [(path, item)];
        });
        await Publish(new FileShareServed(share.Id, account, path, Write: false));
        return [.. entries.Where(entry => SharePath.IsInside(share.Path, entry.Path)).Select(entry => Entry(share, entry.Path, entry.Item))];
    }

    public async Task<byte[]?> Read(string shareId, string? relativePath)
    {
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        var bytes = await Files(files => files.Read(path, BrainWorkspaceFiles.MaxReadBytes));
        await Publish(new FileShareServed(share.Id, account, path, Write: false));
        return bytes;
    }

    public async Task<SharedEntry> Write(string shareId, string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        if (share.Role != ShareRole.Editor) { throw PersonActs.Refused(); }
        // A friend writes no more than a friend can read back.
        if (content.LongLength > BrainWorkspaceFiles.MaxReadBytes) { throw new ArgumentException("The file is larger than a share takes.", nameof(content)); }

        var written = await Files(async files =>
        {
            try { return await files.Write(path, content); }
            catch (DirectoryNotFoundException) { throw new ArgumentException("The folder of that path does not exist.", nameof(relativePath)); }
        });

        var save = new FileShareSave(path, account, Clock.GetUtcNow());
        var saves = State.Saves.Where(existing => existing.Path != path).TakeLast(KeptSaves - 1);
        var next = new FileSharesRecord { Shares = State.Shares, Saves = [.. saves, save] };
        await Save(next, new FileShareServed(share.Id, account, path, Write: true));
        return Entry(share, path, written);
    }

    // The brain's owner, as a person, in this brain.
    private async Task<string> Owner()
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        return brain == Id.Brain ? account : throw PersonActs.Refused();
    }

    // The person the share names, as a person, from the brain they own, through the friend-side relay neuron, and a
    // friend at this moment: a share for a removed friend is refused whether or not the door has heard of the removal.
    private async Task<(WorkspaceShare Share, string Account)> Admit(string shareId)
    {
        var (account, _) = await FriendRelay.Require(Context.Caller, SharedWithMeDefaults.TypeName, Owners);
        var share = State.Shares.FirstOrDefault(candidate => candidate.Id == shareId && candidate.Account == account);
        if (share is null) { throw PersonActs.Refused(); }
        return await Friends.IsFriend(new AccountId(account)) ? (share, account) : throw PersonActs.Refused();
    }

    private Task Commit(WorkspaceShare[] shares, FileShareSave[] saves)
        => Save(new FileSharesRecord { Shares = shares, Saves = saves }, new FileSharesChanged(shares.Length));

    // The workspace is reached as the files module of this brain, never as the person of another brain who asked. A share
    // path ("/work/trip/a.txt") is a workspace path with the old mount prefix.
    private async Task<T> Files<T>(Func<BrainWorkspaceFiles, Task<T>> work)
    {
        using var module = PersonActs.AsFilesModule(services, Id.Brain);
        return await work(new BrainWorkspaceFiles(services.GetRequiredService<DigitalBrain.Capabilities.ICapabilityBroker>(), Id.Brain));
    }

    private SharedEntry Entry(WorkspaceShare share, string path, Item item)
    {
        var relative = path.Length > share.Path.Length ? path[(share.Path.Length + 1)..] : string.Empty;
        var last = State.Saves.LastOrDefault(save => save.Path == path);
        return new SharedEntry(relative, item.Name, item.Kind == ItemKind.Folder, item.Size, last?.At ?? item.Modified, last?.Account ?? string.Empty);
    }
}
