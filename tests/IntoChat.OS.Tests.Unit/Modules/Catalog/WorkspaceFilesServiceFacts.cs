using System.Text;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Modules.Apps;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class WorkspaceFilesServiceFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("metrics.csv", "/work/metrics.csv")]
    [InlineData("/work/data.json", "/work/data.json")]
    [InlineData("/local/notes/todo.txt", "/local/notes/todo.txt")]
    [InlineData("/drive/shared/spec.pdf", "/drive/shared/spec.pdf")]
    [InlineData(null, "/work/artifact.bin")]
    [InlineData("", "/work/artifact.bin")]
    public void ToCanonicalVfsPath_MapsCorrectlyToMounts(string? input, string expected)
    {
        var result = WorkspaceFilesService.ToCanonicalVfsPath(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/work/../escape.txt")]
    [InlineData("/local/../../root.txt")]
    [InlineData("/drive/../../../secrets.json")]
    [InlineData("a/../../b")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("data/sub\0file.txt")]
    [InlineData("folder//file")]
    public void ToCanonicalVfsPath_RejectsTraversalAndHostilePaths(string hostilePath)
    {
        Assert.Throws<ArgumentException>(() => WorkspaceFilesService.ToCanonicalVfsPath(hostilePath));
    }

    [Fact]
    public async Task Operations_RejectTraversalPaths()
    {
        var service = new WorkspaceFilesService(brainId: "safe-brain");
        var content = Encoding.UTF8.GetBytes("hostile attempt");

        await Assert.ThrowsAsync<ArgumentException>(() => service.WriteFileAsync("../escape.txt", content, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadBytesAsync("../escape.txt", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadTextAsync("../escape.txt", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveFileAsync("../escape.txt", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteFileAsync("../escape.txt", Ct));
    }

    [Fact]
    public async Task WriteFileAsync_PersistsToWorkMountAndBuildsLocalDiskNeuronUri()
    {
        var service = new WorkspaceFilesService(brainId: "local");
        var content = Encoding.UTF8.GetBytes("quarter,revenue\nQ1,100\nQ2,150\n");

        var entry = await service.WriteFileAsync("q1_q2_revenue.csv", content, Ct);

        Assert.NotNull(entry);
        Assert.Equal("q1_q2_revenue.csv", entry.Name);
        Assert.Equal("/work/q1_q2_revenue.csv", entry.VfsPath);
        Assert.Equal("local-disk", entry.Connection);
        Assert.Equal("neuron://local/local-disk.files/q1_q2_revenue.csv", entry.NeuronUri);
        Assert.Equal(content.Length, entry.Size);

        // Read back content via VFS
        var readBytes = await service.ReadBytesAsync("q1_q2_revenue.csv", Ct);
        Assert.NotNull(readBytes);
        Assert.Equal(content, readBytes);
    }

    [Fact]
    public async Task ResolveFileAsync_ResolvesByCleanNameVfsPathAndNeuronUri()
    {
        var service = new WorkspaceFilesService(brainId: "local");
        var content = Encoding.UTF8.GetBytes("# Summary\nAll unified.");

        var written = await service.WriteFileAsync("/work/summary.md", content, Ct);
        Assert.NotNull(written);

        // 1. Resolve by clean name
        var byName = await service.ResolveFileAsync("summary.md", Ct);
        Assert.NotNull(byName);
        Assert.Equal(written.VfsPath, byName.VfsPath);

        // 2. Resolve by VFS path
        var byPath = await service.ResolveFileAsync("/work/summary.md", Ct);
        Assert.NotNull(byPath);
        Assert.Equal(written.Name, byPath.Name);

        // 3. Resolve by Neuron URI
        var byUri = await service.ResolveFileAsync(written.NeuronUri, Ct);
        Assert.NotNull(byUri);
        Assert.Equal(written.NeuronUri, byUri.NeuronUri);

        // 4. List files includes it
        var list = await service.ListFilesAsync(ct: Ct);
        Assert.Contains(list, f => f.Name == "summary.md");
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFromStorage()
    {
        var service = new WorkspaceFilesService(brainId: "local");
        var content = Encoding.UTF8.GetBytes("temporary data");

        await service.WriteFileAsync("temp.txt", content, Ct);
        var beforeDelete = await service.ResolveFileAsync("temp.txt", Ct);
        Assert.NotNull(beforeDelete);

        var deleted = await service.DeleteFileAsync("temp.txt", Ct);
        Assert.True(deleted);

        var afterDelete = await service.ResolveFileAsync("temp.txt", Ct);
        Assert.Null(afterDelete);

        var readAfterDelete = await service.ReadBytesAsync("temp.txt", Ct);
        Assert.Null(readAfterDelete);
    }

    [Fact]
    public async Task InstanceIsolation_NoCrossBrainLeakage()
    {
        var brainA = "brain-iso-a-" + Guid.NewGuid().ToString("N");
        var brainB = "brain-iso-b-" + Guid.NewGuid().ToString("N");

        var serviceA = new WorkspaceFilesService(brainId: brainA);
        var serviceB = new WorkspaceFilesService(brainId: brainB);

        var secretData = Encoding.UTF8.GetBytes("brain A exclusive content");
        await serviceA.WriteFileAsync("secret_report.txt", secretData, Ct);

        // Service A sees its file
        var readA = await serviceA.ReadBytesAsync("secret_report.txt", Ct);
        Assert.NotNull(readA);
        Assert.Equal(secretData, readA);

        var resolvedA = await serviceA.ResolveFileAsync("secret_report.txt", Ct);
        Assert.NotNull(resolvedA);

        var listA = await serviceA.ListFilesAsync(ct: Ct);
        Assert.Contains(listA, f => f.Name == "secret_report.txt");

        // Service B (different brain) sees NOTHING from Service A
        var readB = await serviceB.ReadBytesAsync("secret_report.txt", Ct);
        Assert.Null(readB);

        var resolvedB = await serviceB.ResolveFileAsync("secret_report.txt", Ct);
        Assert.Null(resolvedB);

        var listB = await serviceB.ListFilesAsync(ct: Ct);
        Assert.DoesNotContain(listB, f => f.Name == "secret_report.txt");
    }

    [Fact]
    public async Task DiskBackedPersistenceAndReload_SurvivesAcrossServiceInstances()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "test-vfs-reload-" + Guid.NewGuid().ToString("N"));
        var brainId = "brain-persist-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = Encoding.UTF8.GetBytes("persisted payload on real disk");

            // Instance 1 writes
            var service1 = new WorkspaceFilesService(brainId: brainId, rootDirectory: tempRoot);
            var written = await service1.WriteFileAsync("durable.txt", payload, Ct);
            Assert.NotNull(written);

            // Verify physical existence on disk under configured rootDirectory
            var physicalPath = Path.Combine(tempRoot, "work", "durable.txt");
            Assert.True(File.Exists(physicalPath), $"Expected file at {physicalPath}");
            var diskBytes = await File.ReadAllBytesAsync(physicalPath, Ct);
            Assert.Equal(payload, diskBytes);

            // Instance 2 (simulating service restart / fresh DI scope for same brain and root)
            var service2 = new WorkspaceFilesService(brainId: brainId, rootDirectory: tempRoot);

            // Instance 2 reloads and reads bytes from disk
            var reloadedBytes = await service2.ReadBytesAsync("durable.txt", Ct);
            Assert.NotNull(reloadedBytes);
            Assert.Equal(payload, reloadedBytes);

            // Instance 2 resolves file entry from disk
            var reloadedEntry = await service2.ResolveFileAsync("durable.txt", Ct);
            Assert.NotNull(reloadedEntry);
            Assert.Equal("durable.txt", reloadedEntry.Name);
            Assert.Equal("/work/durable.txt", reloadedEntry.VfsPath);

            // Instance 2 lists files from disk
            var list2 = await service2.ListFilesAsync(ct: Ct);
            Assert.Contains(list2, f => f.Name == "durable.txt");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }
}
