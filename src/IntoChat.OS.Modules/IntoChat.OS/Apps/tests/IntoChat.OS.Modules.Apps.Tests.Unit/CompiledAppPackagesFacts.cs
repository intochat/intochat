using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledAppPackagesFacts : IDisposable
{
    private readonly TestHome _home = TestHome.Create();
    private string Root => Path.Combine(_home.Path, "packages");

    [Fact]
    public async Task ANewStoreRestoresTheReviewedArtifactWithoutTheBuildDirectory()
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>());
        await using var archive = await Package();
        var original = await store.Stage(archive, TestContext.Current.CancellationToken);
        Directory.Delete(Path.Combine(_home.Path, "build"), recursive: true);
        using var restarted = new CompiledAppPackages(Root, new Dictionary<string, string>());
        var recovered = await restarted.Read(original.Digest, TestContext.Current.CancellationToken);
        Assert.Equal(original.Digest, recovered.Digest);
        Assert.Equal(original.DirectoryPath, recovered.DirectoryPath);
        Assert.True(File.Exists(Path.Combine(recovered.DirectoryPath, recovered.Descriptor.EntryAssembly)));
    }

    [Fact]
    public async Task ChangedArchiveIsRefusedOnRecovery()
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>());
        await using var archive = await Package();
        var original = await store.Stage(archive, TestContext.Current.CancellationToken);
        var saved = Assert.Single(Directory.GetFiles(Root, "*.intochat-app", SearchOption.AllDirectories));
        File.SetAttributes(saved, FileAttributes.Normal);
        await File.WriteAllBytesAsync(saved, [1, 2, 3], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Read(original.Digest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangedExtractedPayloadIsRefusedOnRecovery()
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>());
        await using var archive = await Package();
        var original = await store.Stage(archive, TestContext.Current.CancellationToken);
        var entry = Path.Combine(original.DirectoryPath, original.Descriptor.EntryAssembly);
        File.SetAttributes(entry, FileAttributes.Normal);
        await File.WriteAllBytesAsync(entry, [1, 2, 3], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Read(original.Digest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentUploadsOfOneDigestHaveOneRecoverableArtifact()
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>());
        await using var package = await Package();
        var bytes = package.ToArray();
        await using var first = new MemoryStream(bytes);
        await using var second = new MemoryStream(bytes);
        var results = await Task.WhenAll(store.Stage(first, TestContext.Current.CancellationToken), store.Stage(second, TestContext.Current.CancellationToken));
        Assert.Equal(results[0].Digest, results[1].Digest);
        Assert.Single(Directory.GetFiles(Root, "*.intochat-app", SearchOption.AllDirectories));
        Assert.Equal(results[0].Digest, (await store.Read(results[0].Digest, TestContext.Current.CancellationToken)).Digest);
    }

    [Fact]
    public async Task OversizedInputLeavesNoRecoverableOrPartialArchive()
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>(), new PackageLimits { MaxArchiveBytes = 32 });
        await using var oversized = new MemoryStream(new byte[33]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.Stage(oversized, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../archive")]
    [InlineData("/absolute")]
    [InlineData("bad")]
    public async Task AnArtifactReferenceCannotEscapeItsCache(string digest)
    {
        using var store = new CompiledAppPackages(Root, new Dictionary<string, string>());
        await Assert.ThrowsAsync<ArgumentException>(() => store.Read(digest, TestContext.Current.CancellationToken));
    }

    private async Task<MemoryStream> Package()
    {
        var build = Directory.CreateDirectory(Path.Combine(_home.Path, "build")).FullName;
        var assembly = typeof(CompiledAppPackagesFacts).Assembly;
        var entry = Path.GetFileName(assembly.Location);
        File.Copy(assembly.Location, Path.Combine(build, entry));
        var descriptor = new CompiledAppDescriptor { Id = "test", Name = "Test", Version = "1", EntryAssembly = entry, EntryType = "NeverLoaded" };
        var archive = new MemoryStream();
        await new AppPackageWriter().Write(descriptor, build, archive, TestContext.Current.CancellationToken);
        archive.Position = 0;
        return archive;
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_home.Path, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
        _home.Dispose();
    }
}
