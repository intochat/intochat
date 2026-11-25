using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Apps;

// The brain's workspace files by path for the screens and other callers of this brain (IWorkspaceFiles). Every call is
// admitted by the caller the runtime stamped: of this brain, or refused. The workspace is reached through the broker under
// that same caller (BrainWorkspaceFiles), so a write is the caller's own and the workspace applies its own write rule.
[Neuron(WorkspaceFilesDefaults.TypeName)]
internal sealed class WorkspaceFilesNeuron(ICapabilityBroker? broker = null) : Neuron, IWorkspaceFiles
{
    public async Task<WorkspaceFile?> Find(string path)
    {
        var files = Files();
        var relative = BrainWorkspaceFiles.Normalize(path);
        return await files.Find(relative).ConfigureAwait(true) is { } item ? Entry(relative, item) : null;
    }

    public async Task<WorkspaceFile[]> List(string folder)
    {
        var files = Files();
        var relative = BrainWorkspaceFiles.Normalize(folder);
        var prefix = relative.Length == 0 ? "" : relative + "/";
        return [.. (await files.List(relative).ConfigureAwait(true)).Select(item => Entry(prefix + item.Name, item))];
    }

    public async Task<byte[]?> Read(string path)
    {
        var files = Files();
        var relative = BrainWorkspaceFiles.Normalize(path);
        if (await files.Find(relative).ConfigureAwait(true) is { Kind: ItemKind.File, Size: > WorkspaceFilesDefaults.MaxReadBytes })
        {
            throw new ArgumentException("That file is larger than a workspace read takes.", nameof(path));
        }
        return await files.Read(relative, WorkspaceFilesDefaults.MaxReadBytes).ConfigureAwait(true);
    }

    public async Task<WorkspaceFile> Write(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var files = Files();
        var relative = BrainWorkspaceFiles.Normalize(path);
        return Entry(relative, await files.Write(relative, content).ConfigureAwait(true));
    }

    private BrainWorkspaceFiles Files()
    {
        RequireSameBrain();
        return new(broker ?? throw new InvalidOperationException("This brain's workspace is not available."), Id.Brain);
    }

    private static WorkspaceFile Entry(string relative, Item item)
        => new(BrainWorkspaceFiles.Stored(relative), item.Name, item.Kind == ItemKind.Folder, item.Kind == ItemKind.Folder ? null : item.Size, item.Modified);
}
