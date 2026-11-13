using System.Text;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using IntoChat.OS.Modules.LocalDisk;
using Xunit;

namespace IntoChat.OS.Modules.LocalDisk.Tests.Unit;

public sealed class LocalDiskStorageFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("sub/nested\0bad.txt")]
    [InlineData("/absolute/path.txt")]
    public async Task StatAndRead_WithHostileItemId_ThrowsDenied(string hostileRelative)
    {
        var tempFolder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("storage-facts-").FullName);
        try
        {
            var rootId = "root-" + Guid.NewGuid().ToString("N");
            var storage = new LocalDiskStorage([new LocalRoot(rootId, tempFolder)]);
            var hostileId = LocalItemId.Of(rootId, hostileRelative);

            await Assert.ThrowsAsync<Denied>(() => storage.Stat(hostileId, Ct));
            await Assert.ThrowsAsync<Denied>(() => storage.Read(hostileId, null, Ct));
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/b.txt")]
    [InlineData("bad\0name.txt")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public async Task OpenWrite_WithUnsafeName_ThrowsArgumentException(string unsafeName)
    {
        var tempFolder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("storage-write-facts-").FullName);
        try
        {
            var rootId = "root-" + Guid.NewGuid().ToString("N");
            var storage = new LocalDiskStorage([new LocalRoot(rootId, tempFolder)]);
            var targetFolder = LocalItemId.Of(rootId, "");
            var plan = new WritePlan(WriteKind.WriteFile, Source: null, TargetFolder: targetFolder, Name: unsafeName, Conflict: ConflictPolicy.Overwrite);

            await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenWrite(plan, Ct));
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task InstanceIsolation_SeparateRootsDoNotLeakFiles()
    {
        var folderA = PathConfinement.Normalize(Directory.CreateTempSubdirectory("storage-iso-a-").FullName);
        var folderB = PathConfinement.Normalize(Directory.CreateTempSubdirectory("storage-iso-b-").FullName);
        try
        {
            var rootIdA = "root-a-" + Guid.NewGuid().ToString("N");
            var rootIdB = "root-b-" + Guid.NewGuid().ToString("N");

            var storageA = new LocalDiskStorage([new LocalRoot(rootIdA, folderA)]);
            var storageB = new LocalDiskStorage([new LocalRoot(rootIdB, folderB)]);

            // Write to storage A
            var planA = new WritePlan(WriteKind.WriteFile, Source: null, TargetFolder: LocalItemId.Of(rootIdA, ""), Name: "isolated.txt", Conflict: ConflictPolicy.Overwrite);
            await using (var writer = await storageA.OpenWrite(planA, Ct))
            {
                await writer.Content.WriteAsync(Encoding.UTF8.GetBytes("storage A data"), Ct);
                _ = await writer.Commit(Ct);
            }

            // Storage A lists the file
            var listA = await storageA.List(LocalItemId.Of(rootIdA, ""), new ListQuery(PageSize: 100), Ct);
            Assert.Contains(listA.Items, item => item.Name == "isolated.txt");

            // Storage B has nothing
            var listB = await storageB.List(LocalItemId.Of(rootIdB, ""), new ListQuery(PageSize: 100), Ct);
            Assert.DoesNotContain(listB.Items, item => item.Name == "isolated.txt");

            // Storage B cannot stat the file under root B
            await Assert.ThrowsAsync<NotFound>(() => storageB.Stat(LocalItemId.Of(rootIdB, "isolated.txt"), Ct));
        }
        finally
        {
            if (Directory.Exists(folderA)) { try { Directory.Delete(folderA, recursive: true); } catch { } }
            if (Directory.Exists(folderB)) { try { Directory.Delete(folderB, recursive: true); } catch { } }
        }
    }

    [Fact]
    public async Task DiskBackedPersistenceAndReload_WritesToDiskAndReloadsAcrossInstances()
    {
        var tempFolder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("storage-persist-").FullName);
        var rootId = "root-" + Guid.NewGuid().ToString("N");
        var expectedBytes = Encoding.UTF8.GetBytes("real disk persistence verification content");

        try
        {
            // Instance 1: write file to disk
            var storage1 = new LocalDiskStorage([new LocalRoot(rootId, tempFolder)]);
            var targetFolder = LocalItemId.Of(rootId, "");
            var plan = new WritePlan(WriteKind.WriteFile, Source: null, TargetFolder: targetFolder, Name: "durable.dat", Conflict: ConflictPolicy.Overwrite);

            await using (var writer = await storage1.OpenWrite(plan, Ct))
            {
                await writer.Content.WriteAsync(expectedBytes, Ct);
                var committedId = await writer.Commit(Ct);
                Assert.NotNull(committedId.Value);
            }

            // Verify file is physically present on disk in root folder
            var physicalPath = Path.Combine(tempFolder, "durable.dat");
            Assert.True(File.Exists(physicalPath), $"Expected file on disk at {physicalPath}");
            var diskBytes = await File.ReadAllBytesAsync(physicalPath, Ct);
            Assert.Equal(expectedBytes, diskBytes);

            // Instance 2: new storage instance pointing to same root (simulating restart / reload)
            var storage2 = new LocalDiskStorage([new LocalRoot(rootId, tempFolder)]);
            var fileId = LocalItemId.Of(rootId, "durable.dat");

            // Read via OpenRead
            await using (var readStream = await storage2.OpenRead(fileId, null, Ct))
            {
                using var ms = new MemoryStream();
                await readStream.Content.CopyToAsync(ms, Ct);
                Assert.Equal(expectedBytes, ms.ToArray());
            }

            // Stat via Stat
            var stat = await storage2.Stat(fileId, Ct);
            Assert.Equal("durable.dat", stat.Name);
            Assert.Equal(ItemKind.File, stat.Kind);
            Assert.Equal(expectedBytes.Length, stat.Size);

            // List via List
            var page = await storage2.List(targetFolder, new ListQuery(PageSize: 100), Ct);
            Assert.Contains(page.Items, item => item.Name == "durable.dat");
        }
        finally
        {
            if (Directory.Exists(tempFolder))
            {
                try { Directory.Delete(tempFolder, recursive: true); } catch { }
            }
        }
    }
}
