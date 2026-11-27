using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Apps;

// The brain's workspace files by path for the screens and other callers of this brain (IWorkspaceFiles). Every call is
// admitted by the caller the runtime stamped: of this brain (and granted workspace.files), or refused. Once admitted, the
// workspace is reached through the broker as the brain's files module (like FileSharesNeuron), never via an implicit kernel lift.
[Neuron(WorkspaceFilesDefaults.TypeName)]
internal sealed class WorkspaceFilesNeuron(ICapabilityBroker? broker = null, IServiceProvider? services = null) : Neuron, IWorkspaceFiles
{
    public Task<WorkspaceFile?> Find(string path) => Run(async files =>
    {
        var relative = BrainWorkspaceFiles.Normalize(path);
        return await files.Find(relative).ConfigureAwait(true) is { } item ? Entry(relative, item) : null;
    });

    public Task<WorkspaceFile[]> List(string folder) => Run(async files =>
    {
        var relative = BrainWorkspaceFiles.Normalize(folder);
        var prefix = relative.Length == 0 ? "" : relative + "/";
        return (await files.List(relative).ConfigureAwait(true)).Select(item => Entry(prefix + item.Name, item)).ToArray();
    });

    public Task<byte[]?> Read(string path) => Run(async files =>
    {
        var relative = BrainWorkspaceFiles.Normalize(path);
        if (await files.Find(relative).ConfigureAwait(true) is { Kind: ItemKind.File, Size: > WorkspaceFilesDefaults.MaxReadBytes })
        {
            throw new ArgumentException("That file is larger than a workspace read takes.", nameof(path));
        }
        return await files.Read(relative, WorkspaceFilesDefaults.MaxReadBytes).ConfigureAwait(true);
    });

    public Task<WorkspaceFile> Write(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Run(async files =>
        {
            var relative = BrainWorkspaceFiles.Normalize(path);
            return Entry(relative, await files.Write(relative, content).ConfigureAwait(true));
        });
    }

    private async Task<T> Run<T>(Func<BrainWorkspaceFiles, Task<T>> work)
    {
        RequireSameBrain();
        var files = new BrainWorkspaceFiles(broker ?? throw new InvalidOperationException("This brain's workspace is not available."), Id.Brain);
        using var module = services is null ? null : PersonActs.AsFilesModule(services, Id.Brain);
        return await work(files).ConfigureAwait(true);
    }

    private static WorkspaceFile Entry(string relative, Item item)
        => new(BrainWorkspaceFiles.Stored(relative), item.Name, item.Kind == ItemKind.Folder, item.Kind == ItemKind.Folder ? null : item.Size, item.Modified);
}
