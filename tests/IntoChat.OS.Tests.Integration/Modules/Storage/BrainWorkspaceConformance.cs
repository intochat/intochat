using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Testing.Conformance.Storage;

namespace IntoChat.OS.Modules.LocalDisk.Tests.Integration;

// The brain's workspace is the confined disk engine with one root, a size cap and the workspace name filter.
public sealed class BrainWorkspaceConformance : StorageConformance<IStorage>
{
    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        var root = PathConfinement.Normalize(Directory.CreateTempSubdirectory("workspace-kit").FullName);
        foreach (var entry in seed.Entries)
        {
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Kind == ItemKind.Folder) { Directory.CreateDirectory(path); }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, entry.Bytes.ToArray(), ct).ConfigureAwait(false);
            }
        }
        var operations = new InMemoryOperations();
        var shape = new LocalDiskShape("Workspace", "local-cloud", LocalCloudNeuron.DefaultMaxStorageBytes, WorkspaceNames.IsHiddenOrSecretSegment);
        var storage = new LocalDiskStorage([new LocalRoot(LocalCloudNeuron.RootId, root)], operations, shape: shape);
        return new StorageSubject(storage, operations, [LocalItemId.Of(LocalCloudNeuron.RootId, "../escape"), LocalItemId.Of(LocalCloudNeuron.RootId, "C:/Windows/win.ini")]);
    }

    protected override ItemId MissingId() => LocalItemId.Of(LocalCloudNeuron.RootId, $"missing-{Guid.NewGuid():N}.txt");
}
