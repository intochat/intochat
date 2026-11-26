using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// storage@1 of a Windows PC, against the unified confined-disk engine over a fresh folder (CAP-04). Each fact gets its own root.
public sealed class WindowsStorageKit : StorageConformance<IStorage>, IDisposable
{
    private readonly List<string> _roots = [];
    private string? _root;

    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        var root = PathConfinement.Normalize(Directory.CreateTempSubdirectory("windows-kit").FullName);
        _roots.Add(root);
        _root = root;
        foreach (var entry in seed.Entries)
        {
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Kind == ItemKind.Folder)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, entry.Bytes.ToArray(), ct).ConfigureAwait(false);
            }
        }

        var operations = new InMemoryOperations();
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(root)], operations: operations, coordinator: new ConfinedDiskCoordinator(), shape: ConfinedDiskShape.Windows);
        var outside = PathConfinement.Normalize(Path.Combine(Directory.GetParent(root)!.FullName, "escape.txt"));
        var windows = PathConfinement.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini"));
        return new StorageSubject(storage, operations, [WindowsPathIdCodec.Of(outside), WindowsPathIdCodec.Of(windows)]);
    }

    protected override ItemId MissingId()
        => WindowsPathIdCodec.Of(Path.Combine(_root ?? throw new InvalidOperationException("Connect runs first."), $"missing-{Guid.NewGuid():N}.txt"));

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            TempDirectory.DeleteIfPresent(root);
        }

        _roots.Clear();
    }
}
