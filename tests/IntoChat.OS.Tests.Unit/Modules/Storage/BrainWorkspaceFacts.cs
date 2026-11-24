using System.Text;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.LocalDisk.Tests.Unit;

// The brain's workspace (local-cloud/main) keeps its files on disk under {home}/brains/{brain}/workspace, survives a restart,
// takes writes without a question only from the brain's person, modules and kernel, and copies the old temp fallback once.
public sealed class BrainWorkspaceFacts : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BrainId Brain = new("alice");
    private static NeuronId WorkspaceId => new(Brain, "local-cloud", "main");

    private readonly string _home = Directory.CreateTempSubdirectory("workspace-home-").FullName;
    private readonly string _temp = Directory.CreateTempSubdirectory("workspace-temp-").FullName;

    private string WorkspaceFolder => Path.Combine(_home, "brains", "alice", "workspace");
    private string LegacyFolder => Path.Combine(_temp, "intochat-vfs", "alice");

    [Fact]
    public async Task AFileWrittenToTheWorkspaceIsThereAfterARestart()
    {
        await using var os = Host();
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var workspace = os.Get<IStorage>(WorkspaceId);
            var root = (await workspace.Roots(Ct))[0];
            await workspace.Write(WritePlan.WriteFile(root.Id, "notes.txt", Encoding.UTF8.GetBytes("kept"), ConflictPolicy.Fail), Ct);
            await Eventually(async () => (await workspace.List(root.Id, new ListQuery(), Ct)).Items.Any(item => item.Name == "notes.txt"));
        }

        await os.Restart();

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var workspace = os.Get<IStorage>(WorkspaceId);
            var root = (await workspace.Roots(Ct))[0];
            var listed = await workspace.List(root.Id, new ListQuery(), Ct);
            var notes = Assert.Single(listed.Items, item => item.Name == "notes.txt");
            Assert.Equal("kept", Encoding.UTF8.GetString((await workspace.Read(notes.Id, null, Ct)).Bytes.Span));
        }
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "notes.txt"), Ct));
    }

    [Fact]
    public async Task AnAppsWriteWaitsForTheOwnerWhileThePersonsOwnWriteDoesNot()
    {
        await using var os = Host();
        os.Confirmations.Policy = DigitalBrain.Confirmation.ConfirmationPolicy.Always;
        var alice = new Actor.Person("alice", "alice");
        ItemId root;
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var workspace = os.Get<IStorage>(WorkspaceId);
            root = (await workspace.Roots(Ct))[0].Id;
            await workspace.Write(WritePlan.WriteFile(root, "mine.txt", Encoding.UTF8.GetBytes("mine"), ConflictPolicy.Fail), Ct);
        }
        await Eventually(() => Task.FromResult(File.Exists(Path.Combine(WorkspaceFolder, "mine.txt"))));
        Assert.Empty(os.Confirmations.Requested);

        Task written;
        using (os.Edge.Enter(os.Edge.App("app.data", alice, Brain)))
        {
            written = os.Get<IStorage>(WorkspaceId).Write(WritePlan.WriteFile(root, "unasked.txt", Encoding.UTF8.GetBytes("x"), ConflictPolicy.Fail), Ct);
        }
        await Eventually(() => Task.FromResult(os.Confirmations.Waiting.Count == 1));
        await os.Confirmations.Deny(os.Confirmations.Waiting[0].PlanId);
        await Assert.ThrowsAnyAsync<Exception>(() => written);
        Assert.False(File.Exists(Path.Combine(WorkspaceFolder, "unasked.txt")));

        using (os.Edge.Enter(os.Edge.Person("bob", "bob", new BrainId("bob"))))
        {
            written = os.Get<IStorage>(WorkspaceId).Write(WritePlan.WriteFile(root, "other-brain.txt", Encoding.UTF8.GetBytes("x"), ConflictPolicy.Fail), Ct);
        }
        // A person of another brain is not this brain's person: refused or held for the owner, never written.
        await Task.WhenAny(written, Task.Delay(500, Ct));
        Assert.False(written.IsCompletedSuccessfully);
        Assert.False(File.Exists(Path.Combine(WorkspaceFolder, "other-brain.txt")));
    }

    [Fact]
    public async Task TheOldTempFallbackIsCopiedInOnceWithoutReplacingOrRemovingAnything()
    {
        Seed(Path.Combine(LegacyFolder, "work", "a.txt"), "legacy a");
        Seed(Path.Combine(LegacyFolder, "work", "sub", "b.txt"), "legacy b");
        Seed(Path.Combine(LegacyFolder, "local", "c.txt"), "legacy c");
        Seed(Path.Combine(LegacyFolder, "drive", "d.txt"), "legacy d");
        Seed(Path.Combine(WorkspaceFolder, "a.txt"), "already in the workspace");
        // A copy interrupted earlier left its staging file behind.
        Seed(Path.Combine(WorkspaceFolder, "sub", ".b.txt.migrating" + LocalDiskStorage.PartialSuffix), "half");

        await using var os = Host();
        await Touch(os);

        Assert.Equal("already in the workspace", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "a.txt"), Ct));
        Assert.Equal("legacy b", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "sub", "b.txt"), Ct));
        Assert.Equal("legacy c", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "Recovered", "local", "c.txt"), Ct));
        Assert.Equal("legacy d", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "Recovered", "drive", "d.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(WorkspaceFolder, "sub", ".b.txt.migrating" + LocalDiskStorage.PartialSuffix)));
        // The originals are left where they were.
        Assert.Equal("legacy a", await File.ReadAllTextAsync(Path.Combine(LegacyFolder, "work", "a.txt"), Ct));
        Assert.Equal("legacy b", await File.ReadAllTextAsync(Path.Combine(LegacyFolder, "work", "sub", "b.txt"), Ct));
        Assert.True(File.Exists(Path.Combine(LegacyFolder, "local", "c.txt")));

        // Done once: a file the person removes afterwards is not brought back by the next activation.
        File.Delete(Path.Combine(WorkspaceFolder, "sub", "b.txt"));
        await os.Restart();
        await Touch(os);
        Assert.False(File.Exists(Path.Combine(WorkspaceFolder, "sub", "b.txt")));
    }

    [Fact]
    public async Task AFileThatCouldNotBeCopiedIsTriedAgainOnTheNextActivation()
    {
        Seed(Path.Combine(LegacyFolder, "work", "locked.txt"), "later");
        Seed(Path.Combine(LegacyFolder, "work", "free.txt"), "now");

        await using var os = Host();
        await using (new FileStream(Path.Combine(LegacyFolder, "work", "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Touch(os);
        }
        Assert.True(File.Exists(Path.Combine(WorkspaceFolder, "free.txt")));
        Assert.False(File.Exists(Path.Combine(WorkspaceFolder, "locked.txt")));

        await os.Restart();
        await Touch(os);
        Assert.Equal("later", await File.ReadAllTextAsync(Path.Combine(WorkspaceFolder, "locked.txt"), Ct));
    }

    [Fact]
    public async Task TheWorkspaceCapCountsTheBytesOnDisk()
    {
        var folder = Directory.CreateTempSubdirectory("workspace-cap-").FullName;
        try
        {
            var shape = new LocalDiskShape("Workspace", "local-cloud", 100, WorkspaceNames.IsHiddenOrSecretSegment);
            var storage = new LocalDiskStorage([new LocalRoot("workspace", folder)], shape: shape);
            var root = LocalItemId.Of("workspace", "");
            await File.WriteAllBytesAsync(Path.Combine(folder, "already.bin"), new byte[40], Ct);

            await Commit(storage, root, "first.bin", 50, ConflictPolicy.Fail);
            Assert.Equal(90, (await storage.Describe(Ct)).Quota.Used);
            await Assert.ThrowsAsync<QuotaExceeded>(() => Commit(storage, root, "second.bin", 20, ConflictPolicy.Fail));
            await Commit(storage, root, "first.bin", 60, ConflictPolicy.Overwrite);

            var described = await storage.Describe(Ct);
            Assert.Equal(QuotaState.Known, described.Quota.State);
            Assert.Equal(100, described.Quota.Used);
            Assert.Equal(100, described.Quota.Total);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task TheWorkspaceNeverShowsDotFilesOrSecrets()
    {
        var folder = Directory.CreateTempSubdirectory("workspace-names-").FullName;
        try
        {
            Seed(Path.Combine(folder, "readme.md"), "# Workspace");
            Seed(Path.Combine(folder, ".env"), "SECRET_KEY=topsecret");
            Seed(Path.Combine(folder, "private.pem"), "PRIVATE KEY");
            Seed(Path.Combine(folder, ".git", "config"), "git config");
            var shape = new LocalDiskShape("Workspace", "local-cloud", null, WorkspaceNames.IsHiddenOrSecretSegment);
            var storage = new LocalDiskStorage([new LocalRoot("workspace", folder)], shape: shape);
            var root = LocalItemId.Of("workspace", "");

            var listed = (await storage.List(root, new ListQuery(), Ct)).Items.Select(item => item.Name).ToArray();

            Assert.Equal(["readme.md"], listed);
            await Assert.ThrowsAsync<NotFound>(() => storage.Read(LocalItemId.Of("workspace", ".env"), null, Ct));
            await Assert.ThrowsAsync<NotFound>(() => storage.Read(LocalItemId.Of("workspace", ".git/config"), null, Ct));
            await Assert.ThrowsAsync<Denied>(() => storage.Read(LocalItemId.Of("workspace", "../escape.txt"), null, Ct));
            Assert.Equal("Workspace", (await storage.Describe(Ct)).Label);
            Assert.Equal(["readme.md"], (await storage.Search(new SearchQuery("e"), Ct)).Items.Select(item => item.Name).ToArray());
            await Assert.ThrowsAsync<Denied>(() => Commit(storage, root, ".env.local", 1, ConflictPolicy.Fail));
            await Assert.ThrowsAsync<Denied>(() => Commit(storage, root, "server.key", 1, ConflictPolicy.Fail));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _home, _temp })
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    private TestOS Host()
    {
        var services = new ServiceCollection()
            .AddSingleton<IOperations>(new InMemoryOperations())
            .AddSingleton(new BrainWorkspaceRoots(_home, _temp))
            .BuildServiceProvider();
        return TestOS.Local(o => o.Services = services).Register<LocalCloudNeuron>();
    }

    private static async Task Touch(TestOS os)
    {
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            _ = await os.Get<IStorage>(WorkspaceId).Roots(Ct);
        }
    }

    private static async Task Commit(LocalDiskStorage storage, ItemId root, string name, int bytes, ConflictPolicy conflict)
    {
        var plan = WritePlan.WriteFile(root, name, ReadOnlyMemory<byte>.Empty, conflict) with { Inline = null };
        await using var writer = await storage.OpenWrite(plan, Ct);
        await writer.Content.WriteAsync(new byte[bytes], Ct);
        _ = await writer.Commit(Ct);
    }

    private static void Seed(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) { return; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The condition did not hold within five seconds.");
    }
}
