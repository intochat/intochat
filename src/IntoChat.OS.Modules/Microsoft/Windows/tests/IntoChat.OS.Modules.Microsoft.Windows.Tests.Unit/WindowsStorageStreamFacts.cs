using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

// 05-08: WindowsStorage on the PC itself streams files from a FileStream (seekable whole, bounded for a range) and refuses
// writes while it does not offer Write. The kit reaches it through the broker; these facts hold the host-local streams.
public sealed class WindowsStorageStreamFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenReadStreamsTheWholeFileSeekableAndARangeBounded()
    {
        var root = Directory.CreateTempSubdirectory("windows-stream").FullName;
        var path = Path.Combine(root, "plan.txt");
        await File.WriteAllBytesAsync(path, [.. Enumerable.Range(0, 16).Select(value => (byte)value)], Ct);
        var storage = new WindowsStorage([MachineRoots.Normalize(root)]);
        var id = WindowsStorage.IdOf(MachineRoots.Normalize(path));

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
    public async Task WritesAreUnsupportedAndNotOffered()
    {
        var root = Directory.CreateTempSubdirectory("windows-stream").FullName;
        var storage = new WindowsStorage([MachineRoots.Normalize(root)]);
        var folder = WindowsStorage.IdOf(MachineRoots.Normalize(root));

        Assert.Equal(ItemFlags.Read | ItemFlags.Search, (await storage.Describe(Ct)).Flags);
        Assert.Equal("unsupported", (await Assert.ThrowsAsync<Unsupported>(() => storage.OpenWrite(WritePlan.WriteFile(folder, "x.txt"), Ct))).Code);
        await Assert.ThrowsAsync<Unsupported>(() => storage.Write(WritePlan.CreateFolder(folder, "x"), Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task AnIdOutsideTheRootsIsDeniedAndAForeignIdNotFound()
    {
        var root = Directory.CreateTempSubdirectory("windows-stream").FullName;
        var storage = new WindowsStorage([MachineRoots.Normalize(root)]);

        Assert.Equal("denied", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.Stat(WindowsStorage.IdOf(Path.Combine(root, "..", "escape.txt")), Ct))).Code);
        Assert.Equal("not-found", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.Stat(new ItemId("drive-file-id"), Ct))).Code);
    }
}
