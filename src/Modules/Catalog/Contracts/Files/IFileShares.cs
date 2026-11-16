using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public enum ShareRole
{
    Viewer = 0,
    Editor = 1,
}

// One folder or file of the workspace, shared with one person. Path is canonical ("/work/..."); a shared file has nothing beneath it.
public sealed record WorkspaceShare(string Id, string Account, string Path, bool IsFolder, ShareRole Role, DateTimeOffset SharedAt);

// An entry under a share. Path is relative to the share; ModifiedBy is the account whose save through the share was last, or empty.
public sealed record SharedEntry(string Path, string Name, bool IsFolder, long? Size, DateTimeOffset? Modified, string ModifiedBy);

[DigitalBrain.Signal("files.share.served")]
public sealed record FileShareServed(string ShareId, string Account, string Path, bool Write) : DigitalBrain.Signal;

[DigitalBrain.Signal("files.shares.changed")]
public sealed record FileSharesChanged(int Count) : DigitalBrain.Signal;

public static class FileSharesDefaults
{
    public const string TypeName = "files.shares";
    public const string KeyPart = "files/shares";
    public static DigitalBrain.NeuronId Id(string ownerBrain) => new(new DigitalBrain.BrainId(ownerBrain), TypeName, KeyPart);
}

// The door of a brain's shared files: the one contract a person of another brain can reach. It holds the list of shares
// and decides every request from the kernel-stamped caller and that list, with no cache.
[Shareable, NeuronAlias(FileSharesDefaults.TypeName), DefaultNeuronType(FileSharesDefaults.TypeName)]
public interface IFileShares
{
    // The brain's owner, acting as a person. An app, an agent or a unit is refused.
    Task<WorkspaceShare> Share(string account, string path, ShareRole role);
    Task Unshare(string shareId);
    Task UnshareAccount(string account);
    [ReadOnlyCall] Task<WorkspaceShare[]> Shares();

    // This brain's friend links, when a friend is removed: every share made for that account goes.
    [Internal] Task DropAccount(string account);

    // The person a share names, acting as a person from the brain they own.
    [Shared] Task<WorkspaceShare[]> SharedWithCaller();
    [Shared] Task<WorkspaceShare> Describe(string shareId);
    [Shared] Task<SharedEntry[]> List(string shareId, string? relativePath);
    [Shared] Task<byte[]?> Read(string shareId, string? relativePath);
    [Shared] Task<SharedEntry> Write(string shareId, string relativePath, byte[] content);
}

public sealed record SharedPointer(string OwnerBrain, string OwnerAccount, string ShareId, string Path, bool IsFolder, ShareRole Role);

public static class SharedWithMeDefaults
{
    public const string TypeName = "files.shared-with-me";
    public const string KeyPart = "files/shared-with-me";
    public static string Key(string brain) => $"{brain}/{KeyPart}";
    public static DigitalBrain.NeuronId Id(string brain) => new(new DigitalBrain.BrainId(brain), TypeName, KeyPart);
}

// A person's side of the shares others made for them: the pointers, and the route their requests take to an owner's door.
// A pointer grants nothing; the owner's door decides each call.
[NeuronAlias(SharedWithMeDefaults.TypeName), DefaultNeuronType(SharedWithMeDefaults.TypeName)]
public interface ISharedWithMe
{
    // Asks the door of every confirmed friend what it shares with this brain's owner, and replaces the pointers with the
    // answers. The doors are never the ones to call: a door and this neuron calling each other could wait on each other.
    Task<SharedPointer[]> Refresh();

    Task<SharedPointer> Add(string ownerBrain, string shareId);
    Task Forget(string ownerBrain, string shareId);
    [ReadOnlyCall] Task<SharedPointer[]> Pointers();
    Task<SharedEntry[]> List(string ownerBrain, string shareId, string? relativePath);
    Task<byte[]?> Read(string ownerBrain, string shareId, string? relativePath);
    Task<SharedEntry> Write(string ownerBrain, string shareId, string relativePath, byte[] content);
}

// Who owns a brain. The product answers from the identity capability.
public interface IBrainOwners
{
    Task<string?> OwnerAccount(DigitalBrain.BrainId brain, CancellationToken cancellationToken = default);
}
