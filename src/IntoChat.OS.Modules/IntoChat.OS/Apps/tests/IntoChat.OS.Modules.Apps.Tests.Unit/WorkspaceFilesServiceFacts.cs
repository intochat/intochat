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
    public async Task DeleteFileAsync_RemovesFromStorageAndOverlay()
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
}
