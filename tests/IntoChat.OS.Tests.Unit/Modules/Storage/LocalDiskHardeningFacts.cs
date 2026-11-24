using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.LocalDisk.Tests.Unit;

// The local disk neuron builds a storage engine per call; writes to one file must not collide, the roots are changed only
// by the person of the brain or the host, and the quota counts every volume the roots are on.
public sealed class LocalDiskHardeningFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BrainId Brain = new("alice");
    private static NeuronId DiskId => new(Brain, LocalDiskNeuron.TypeName, LocalDiskNeuron.KeyName);

    [Fact]
    public async Task TwoOpenWritesToTheSameFileStageApartAndTheLastCommitWins()
    {
        var folder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("localdisk-stage-").FullName);
        try
        {
            var rootId = Guid.NewGuid().ToString("N");
            var coordinator = new LocalDiskCoordinator();
            // Two engines, as the neuron builds one per call.
            var first = new LocalDiskStorage([new LocalRoot(rootId, folder)], coordinator: coordinator);
            var second = new LocalDiskStorage([new LocalRoot(rootId, folder)], coordinator: coordinator);
            var plan = WritePlan.WriteFile(LocalItemId.Of(rootId, ""), "same.txt", ReadOnlyMemory<byte>.Empty, ConflictPolicy.Overwrite) with { Inline = null };

            await using var a = await first.OpenWrite(plan, Ct);
            await using var b = await second.OpenWrite(plan, Ct);
            await a.Content.WriteAsync(Encoding.UTF8.GetBytes("first"), Ct);
            await b.Content.WriteAsync(Encoding.UTF8.GetBytes("second"), Ct);
            _ = await a.Commit(Ct);
            _ = await b.Commit(Ct);

            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(folder, "same.txt"), Ct));
            Assert.Empty(Directory.EnumerateFiles(folder, "*" + LocalDiskStorage.PartialSuffix, SearchOption.AllDirectories));
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task AWriteThatKeepsBothNamesOneFileWhileAnotherIsStaged()
    {
        var folder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("localdisk-rename-").FullName);
        try
        {
            var rootId = Guid.NewGuid().ToString("N");
            var coordinator = new LocalDiskCoordinator();
            var first = new LocalDiskStorage([new LocalRoot(rootId, folder)], coordinator: coordinator);
            var second = new LocalDiskStorage([new LocalRoot(rootId, folder)], coordinator: coordinator);
            var plan = WritePlan.WriteFile(LocalItemId.Of(rootId, ""), "note.txt", ReadOnlyMemory<byte>.Empty, ConflictPolicy.Rename) with { Inline = null };

            await using var a = await first.OpenWrite(plan, Ct);
            await using var b = await second.OpenWrite(plan, Ct);
            await a.Content.WriteAsync(Encoding.UTF8.GetBytes("a"), Ct);
            await b.Content.WriteAsync(Encoding.UTF8.GetBytes("b"), Ct);
            _ = await a.Commit(Ct);
            _ = await b.Commit(Ct);

            var names = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["note (2).txt", "note.txt"], names);
        }
        finally { Delete(folder); }
    }

    [Fact]
    public async Task TheQuotaCountsEveryVolumeTheRootsAreOn()
    {
        var volumes = new TwoVolumes();
        var storage = new LocalDiskStorage([new LocalRoot("c", @"C:\work"), new LocalRoot("d", @"D:\data")], volumes: volumes);

        var described = await storage.Describe(Ct);

        Assert.Equal(QuotaState.Known, described.Quota.State);
        Assert.Equal(70 + 200, described.Quota.Used);
        Assert.Equal(100 + 1000, described.Quota.Total);
    }

    [Fact]
    public async Task TwoRootsOnOneVolumeCountItOnce()
    {
        var storage = new LocalDiskStorage([new LocalRoot("c", @"C:\work"), new LocalRoot("c2", @"C:\other")], volumes: new TwoVolumes());

        var described = await storage.Describe(Ct);

        Assert.Equal(70, described.Quota.Used);
        Assert.Equal(100, described.Quota.Total);
    }

    [Fact]
    public async Task OnlyThePersonOfTheBrainOrTheHostChangesTheRoots()
    {
        var folder = PathConfinement.Normalize(Directory.CreateTempSubdirectory("localdisk-roots-").FullName);
        try
        {
            await using var os = Host();
            var disk = os.Get<ILocalDisk>(DiskId);
            var alice = new Actor.Person("alice", "alice");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.AllowRoot(folder));
            using (os.Edge.Enter(os.Edge.App("app.files", alice, Brain)))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.AllowRoot(folder)); }
            using (os.Edge.Enter(os.Edge.Agent(alice, Brain)))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.AllowRoot(folder)); }
            using (os.Edge.Enter(os.Edge.Person("bob", "bob", new BrainId("bob"))))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.AllowRoot(folder)); }
            using (os.Edge.Enter(os.Edge.Module("apps.files", new BrainId("bob"))))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.AllowRoot(folder)); }

            using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
            {
                Assert.Empty(await disk.Roots());
                await disk.AllowRoot(folder);
            }
            using (os.Edge.Enter(os.Edge.Module("apps.files", Brain)))
            {
                Assert.Equal([folder], await disk.Roots());
            }

            using (os.Edge.Enter(os.Edge.App("app.files", alice, Brain)))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => disk.RevokeRoot(folder)); }
            using (os.Edge.Enter(os.Edge.Kernel(Brain)))
            {
                await disk.RevokeRoot(folder);
                Assert.Empty(await disk.Roots());
            }
        }
        finally { Delete(folder); }
    }

    private static TestOS Host()
    {
        var services = new ServiceCollection()
            .AddSingleton<IOperations>(new InMemoryOperations())
            .AddSingleton(new LocalDiskCoordinator())
            .BuildServiceProvider();
        return TestOS.Local(o => o.Services = services).Register<LocalDiskNeuron>();
    }

    private static void Delete(string folder)
    {
        try { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
        catch (IOException) { }
    }

    private sealed class TwoVolumes : IVolumeSource
    {
        private static readonly VolumeRow C = new(@"C:\", "NTFS", true, DriveType.Fixed, 30, 100);
        private static readonly VolumeRow D = new(@"D:\", "NTFS", true, DriveType.Fixed, 800, 1000);
        public IReadOnlyList<VolumeRow> Drives() => [C, D];
        public VolumeRow? ForPath(string path) => path.StartsWith('D') ? D : C;
    }
}
