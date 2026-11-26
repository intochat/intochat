using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// 05-08: Windows storage on the PC itself streams files from a FileStream (seekable whole, bounded for a range) and refuses
// staged writes publish on commit. The kit reaches it through the broker; these facts hold the host-local streams.
public sealed class WindowsStorageStreamFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenReadStreamsTheWholeFileSeekableAndARangeBounded()
    {
        using var temp = new TempDirectory("windows-stream-");
        var root = temp.Path;
        var path = Path.Combine(root, "plan.txt");
        await File.WriteAllBytesAsync(path, [.. Enumerable.Range(0, 16).Select(value => (byte)value)], Ct);
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(PathConfinement.Normalize(root))], coordinator: new ConfinedDiskCoordinator(), shape: ConfinedDiskShape.Windows);
        var id = WindowsPathIdCodec.Of(PathConfinement.Normalize(path));

        await using (var whole = await storage.OpenRead(id, null, Ct))
        {
            Assert.True(whole.CanSeek);
            Assert.Equal(16, whole.Length);
            Assert.Equal("text/plain", whole.ContentType);
            Assert.False(string.IsNullOrEmpty(whole.ETag));
            Assert.True(whole.Content.CanSeek);
        }

        await using var range = await storage.OpenRead(id, new ByteRange(2, 10), Ct);
        using var copy = new MemoryStream();
        await range.Content.CopyToAsync(copy, Ct);
        Assert.Equal(10, range.Length);
        Assert.Equal(Enumerable.Range(2, 10).Select(value => (byte)value), copy.ToArray());
    }

    [Fact]
    public async Task RootFlagsOfferCreationButNeverRootDestruction()
    {
        using var temp = new TempDirectory("windows-stream-");
        var root = temp.Path;
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(PathConfinement.Normalize(root))], coordinator: new ConfinedDiskCoordinator(), shape: ConfinedDiskShape.Windows);
        var folder = WindowsPathIdCodec.Of(PathConfinement.Normalize(root));

        Assert.True((await storage.Describe(Ct)).Flags.HasFlag(ItemFlags.Write | ItemFlags.CreateFolder | ItemFlags.Trash));
        Assert.Equal(ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder, (await storage.Stat(folder, Ct)).Flags);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task AnIdOutsideTheRootsIsDeniedAndAForeignIdNotFound()
    {
        using var temp = new TempDirectory("windows-stream-");
        var root = temp.Path;
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(PathConfinement.Normalize(root))], coordinator: new ConfinedDiskCoordinator(), shape: ConfinedDiskShape.Windows);

        Assert.Equal("denied", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.Stat(WindowsPathIdCodec.Of(Path.Combine(root, "..", "escape.txt")), Ct))).Code);
        Assert.Equal("not-found", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.Stat(new ItemId("drive-file-id"), Ct))).Code);
    }

    [Fact]
    public async Task WindowsShapePreservesWireIdFormatDescriptorAndIntochatTrashSemantics()
    {
        using var temp = new TempDirectory("windows-trash-");
        var root = PathConfinement.Normalize(temp.Path);
        var file = Path.Combine(root, "doc.txt");
        await File.WriteAllTextAsync(file, "kept", Ct);
        var operations = new DigitalBrain.Testing.Conformance.Storage.InMemoryOperations();
        var storage = new ConfinedDiskStorage([LocalRoot.FromPath(root)], operations: operations, coordinator: new ConfinedDiskCoordinator(), shape: ConfinedDiskShape.Windows);

        var described = await storage.Describe(Ct);
        Assert.Equal("This PC", described.Label);
        Assert.Equal("windows", described.Kind);

        var expectedWire = "w:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(file)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var fileId = WindowsPathIdCodec.Of(file);
        Assert.Equal(expectedWire, fileId.Value);

        var opId = await storage.Write(WritePlan.Delete(fileId, permanent: false), Ct);
        var result = await operations.WaitFor(opId, Ct);
        Assert.IsType<DigitalBrain.Operations.OperationResult.Succeeded>(result);

        var trashDir = Path.Combine(root, ".intochat-trash");
        Assert.True(Directory.Exists(trashDir));
        Assert.NotEmpty(Directory.EnumerateFiles(trashDir));
        Assert.Empty((await storage.List(WindowsPathIdCodec.Of(root), new ListQuery(), Ct)).Items);
        Assert.Empty((await storage.Search(new SearchQuery("doc"), Ct)).Items);
        Assert.Equal("denied", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.Stat(WindowsPathIdCodec.Of(trashDir), Ct))).Code);
    }
}
