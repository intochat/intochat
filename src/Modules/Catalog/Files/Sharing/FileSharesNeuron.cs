using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.State;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

[ExportableState]
internal sealed class FileSharesRecord
{
    public FileShare[] Shares { get; set; } = [];
    // The last save made through a share, per canonical path: who and when.
    public FileShareSave[] Saves { get; set; } = [];
}

internal sealed record FileShareSave(string Path, string Account, DateTimeOffset At);

// The door of a brain's shared files, at "{brain}/files/shares". Its own list is the only authority: a removed share is
// refused on the next call. It reads and writes the workspace by exact path, as the brain's files module.
[Neuron(FileSharesDefaults.TypeName)]
internal sealed class FileSharesNeuron(IServiceProvider services) : Neuron<FileSharesRecord>, IFileShares
{
    private const int KeptSaves = 500;
    private const string FilesModule = "apps.files";

    private IBrainOwners Owners => services.GetRequiredService<IBrainOwners>();
    private TimeProvider Clock => Context.Time;

    public async Task<FileShare> Share(string account, string path, ShareRole role)
    {
        var owner = await Owner();
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        if (!Enum.IsDefined(role)) { throw new ArgumentException("The role is Viewer or Editor.", nameof(role)); }
        if (string.Equals(account, owner, StringComparison.Ordinal)) { throw new ArgumentException("A share names another person.", nameof(account)); }

        var canonical = SharePath.Normalize(path);
        var item = await Files(vfs => vfs.Stat(canonical))
            ?? throw new ArgumentException("There is no file or folder at that path.", nameof(path));

        var share = new FileShare(Guid.NewGuid().ToString("N"), account, canonical, item.Kind == ItemKind.Folder, role, Clock.GetUtcNow());
        var kept = State.Shares.Where(existing => existing.Account != account || existing.Path != canonical);
        await Commit([.. kept, share], State.Saves);
        return share;
    }

    public async Task Unshare(string shareId)
    {
        await Owner();
        var kept = State.Shares.Where(share => share.Id != shareId).ToArray();
        if (kept.Length != State.Shares.Length) { await Commit(kept, State.Saves); }
    }

    public async Task UnshareAccount(string account)
    {
        await Owner();
        var kept = State.Shares.Where(share => share.Account != account).ToArray();
        if (kept.Length != State.Shares.Length) { await Commit(kept, State.Saves); }
    }

    public async Task<FileShare[]> Shares()
    {
        await Owner();
        return State.Shares;
    }

    public async Task<FileShare> Describe(string shareId) => (await Admit(shareId)).Share;

    public async Task<SharedEntry[]> List(string shareId, string? relativePath)
    {
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        var entries = await Files<IReadOnlyList<VfsItem>>(async vfs =>
        {
            if (await vfs.Stat(path) is not { } item) { return []; }
            return item.Kind == ItemKind.Folder ? await vfs.List(path) : [item];
        });
        await Publish(new FileShareServed(share.Id, account, path, Write: false));
        return [.. entries.Where(entry => SharePath.IsInside(share.Path, entry.Path)).Select(entry => Entry(share, entry))];
    }

    public async Task<byte[]?> Read(string shareId, string? relativePath)
    {
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        var bytes = await Files(async vfs =>
        {
            if (await vfs.Stat(path) is not { Kind: not ItemKind.Folder }) { return null; }
            return (await vfs.Read(path, new ByteRange(0, WorkspaceFilesService.MaxReadBytes))).Bytes.ToArray();
        });
        await Publish(new FileShareServed(share.Id, account, path, Write: false));
        return bytes;
    }

    public async Task<SharedEntry> Write(string shareId, string relativePath, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (share, account) = await Admit(shareId);
        var path = SharePath.Resolve(share.Path, share.IsFolder, relativePath);
        if (share.Role != ShareRole.Editor) { throw PersonActs.Refused(); }

        var written = await Files(async vfs =>
        {
            if (await vfs.Stat(path) is { Kind: ItemKind.Folder }) { throw new ArgumentException("That path is a folder.", nameof(relativePath)); }
            try { await vfs.Write(path, content); }
            catch (DirectoryNotFoundException) { throw new ArgumentException("The folder of that path does not exist.", nameof(relativePath)); }
            return await vfs.Stat(path) ?? throw new IOException("The file was not written.");
        });

        var save = new FileShareSave(path, account, Clock.GetUtcNow());
        var saves = State.Saves.Where(existing => existing.Path != path).TakeLast(KeptSaves - 1);
        var next = new FileSharesRecord { Shares = State.Shares, Saves = [.. saves, save] };
        await Save(next, new FileShareServed(share.Id, account, path, Write: true));
        return Entry(share, written);
    }

    // The brain's owner, as a person, in this brain.
    private async Task<string> Owner()
    {
        var (account, brain) = await PersonActs.Require(Context.Caller, Owners);
        return brain == Id.Brain ? account : throw PersonActs.Refused();
    }

    // The person the share names, as a person, from the brain they own.
    private async Task<(FileShare Share, string Account)> Admit(string shareId)
    {
        var (account, _) = await PersonActs.Require(Context.Caller, Owners);
        var share = State.Shares.FirstOrDefault(candidate => candidate.Id == shareId && candidate.Account == account);
        return share is null ? throw PersonActs.Refused() : (share, account);
    }

    private Task Commit(FileShare[] shares, FileShareSave[] saves)
        => Save(new FileSharesRecord { Shares = shares, Saves = saves }, new FileSharesChanged(shares.Length));

    // Storage is reached as the files module of this brain, never as the person of another brain who asked.
    private async Task<T> Files<T>(Func<IStorageVfs, Task<T>> work)
    {
        var edge = services.GetService<EdgeAuthority>();
        using var module = edge?.Enter(edge.Module(FilesModule, Id.Brain));
        return await WorkspaceFilesService.ForBrain(services, Id.Brain.Value).WithVfs(work);
    }

    private SharedEntry Entry(FileShare share, VfsItem item)
    {
        var relative = item.Path.Length > share.Path.Length ? item.Path[(share.Path.Length + 1)..] : string.Empty;
        var by = State.Saves.LastOrDefault(save => save.Path == item.Path)?.Account ?? string.Empty;
        return new SharedEntry(relative, item.Name, item.Kind == ItemKind.Folder, item.Size, item.Modified, by);
    }
}
