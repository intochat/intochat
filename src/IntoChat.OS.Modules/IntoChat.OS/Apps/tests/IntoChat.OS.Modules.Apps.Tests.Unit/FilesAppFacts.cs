using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.Files;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class FilesAppFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FrameworkAppInstallsAndRunsWithoutProviderOrOrleansKnowledge()
    {
        var storage = new InMemoryStorage("Documents");
        using var services = new ServiceCollection().AddSingleton(typeof(BrainId), new BrainId("owner")).AddSingleton(new FilesState()).BuildServiceProvider();
        await using var os = TestOS.Local(options => options.Services = services).With<IStorage>("documents", storage);
        var app = await os.Install<FilesApp>();
        await app.Request("open");
        Assert.Equal("files", app.Manifest.Id);
        Assert.Equal([Capability.Storage.V1], app.Manifest.Requires.Capabilities);
        Assert.Equal("documents", Assert.Single(app.App.Snapshot.Storages).Label);
    }

    [Fact]
    public async Task FolderPagingDoesNotLoseItemsAndAncestorsIncludeCurrentFolderOnce()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "documents");
        for (var n = 0; n < 105; n++) { storage.AddFile(folder, $"file-{n:D3}.txt", [1]); }
        var app = App(storage);
        await app.Browse(new("a", folder), Ct);
        Assert.Equal(40, app.Snapshot.Items.Length);
        while (app.Snapshot.Pages.Any(page => !page.Complete)) { await app.More(new(), Ct); }
        Assert.Equal(105, app.Snapshot.Items.Length);
        Assert.Equal(folder, app.Snapshot.Breadcrumbs[^1].Item.Id);
        Assert.Single(app.Snapshot.Breadcrumbs, item => item.Item.Id == folder);
    }

    [Fact]
    public async Task SearchAcrossStoragesKeepsTheSourceConnectionOnEveryResult()
    {
        var a = new InMemoryStorage("a");
        var b = new InMemoryStorage("b");
        a.AddFile(a.AddFolder(a.Root, "nested"), "report.txt", [1]);
        b.AddFile(b.Root, "report.txt", [2]);
        var app = App(a, b);
        await app.Search(new("report", Everywhere: true), Ct);
        Assert.Equal(["a", "b"], app.Snapshot.Items.Select(item => item.Connection));
        await app.Select(new(app.Snapshot.Items[1]), Ct);
        Assert.Equal("b", app.Snapshot.Selected!.Connection);
    }

    [Fact]
    public async Task PlansDefaultToFailAndTrashAndCarryExplicitSourceAndDestination()
    {
        var a = new InMemoryStorage("a");
        var b = new InMemoryStorage("b");
        var file = a.AddFile(a.Root, "report.txt", [1]);
        var app = App(a, b);
        await app.Browse(new("a", a.Root), Ct);
        await app.Select(new(new("a", await a.Stat(file, Ct))), Ct);
        var delete = app.Plan("delete");
        Assert.False(delete.Permanent);
        Assert.Equal(ConflictPolicy.Fail, delete.Conflict);
        app.Copy(move: true);
        await app.Browse(new("b", b.Root), Ct);
        var move = app.Plan("paste");
        Assert.Equal(WriteKind.Move, move.Kind);
        Assert.Equal(file, move.Source);
        Assert.Equal(b.Root, move.TargetFolder);
        app.Options(ConflictPolicy.Overwrite, dryRun: true);
        Assert.True(app.Plan("paste").DryRun);
        Assert.Equal(ConflictPolicy.Overwrite, app.Plan("paste").Conflict);
    }

    [Fact]
    public async Task ReadOnlyStorageCannotProduceWritePlans()
    {
        var storage = new InMemoryStorage(flags: ItemFlags.Read | ItemFlags.Search);
        var app = App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        Assert.Throws<Unsupported>(() => app.Plan("mkdir", "blocked"));
        Assert.Throws<Unsupported>(() => app.Plan("write", "blocked.txt", "data"));
    }

    [Fact]
    public async Task EmptyConnectionsHaveAnActionableState()
    {
        var app = new FilesApp(new Broker([]), new("owner"), new());
        await app.Open(new(), Ct);
        Assert.Empty(app.Snapshot.Storages);
        Assert.Contains("Settings", app.Snapshot.Message!, StringComparison.Ordinal);
    }

    private static FilesApp App(IStorage a, IStorage? b = null) => new(new Broker(b is null ? [new("a", "A", a)] : [new("a", "A", a), new("b", "B", b)]), new("owner"), new());

    [Fact]
    public async Task AccountLabelsAndHealthyResultsSurviveAnUnavailableStorage()
    {
        var healthy = new InMemoryStorage("Google Drive");
        healthy.AddFile(healthy.Root, "report.txt", [1]);
        var failed = new ScriptedStorage { FailSearch = true };
        var app = App(healthy, failed);
        await app.Open(new(), Ct);
        Assert.Equal(["A", "B"], app.Snapshot.Storages.Select(item => item.Label));
        await app.Search(new("report", Everywhere: true), Ct);
        Assert.Equal("a", Assert.Single(app.Snapshot.Items).Connection);
        Assert.Contains("B:", app.Snapshot.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFilteredSearchPageStillLoadsLaterMatchesAndClearsEmptyMessage()
    {
        var storage = new ScriptedStorage { EmptyFirstPage = true };
        storage.AddFile(storage.Root, "report.txt", [1]);
        var app = App(storage);
        await app.Search(new("report", Everywhere: true), Ct);
        Assert.Empty(app.Snapshot.Items);
        Assert.False(Assert.Single(app.Snapshot.Pages).Complete);
        await app.More(new(), Ct);
        Assert.Single(app.Snapshot.Items);
        Assert.Null(app.Snapshot.Message);
    }

    [Fact]
    public async Task FolderAndFileShortcutsResolveTheirTargetsForBrowseAndPreview()
    {
        var storage = new ScriptedStorage();
        var folder = storage.AddFolder(storage.Root, "documents");
        var file = storage.AddFile(folder, "report.txt", "hello"u8);
        storage.Link = new(new("shortcut"), "Shortcut", ItemKind.Link, null, null, ItemFlags.Read, Target: folder);
        var app = App(storage);
        await app.Browse(new("a", storage.Link.Id), Ct);
        Assert.Equal(folder, app.Snapshot.Folder);
        storage.Link = storage.Link with { Target = file };
        await app.Select(new(new("a", storage.Link)), Ct);
        await app.Preview(new(), Ct);
        Assert.Equal("hello", app.Snapshot.Preview);
    }

    private sealed class ScriptedStorage : InMemoryStorage, IStorage
    {
        public bool FailSearch { get; init; }
        public bool EmptyFirstPage { get; init; }
        public Item? Link { get; set; }
        Task<Item> IStorage.Stat(ItemId id, CancellationToken ct) => Link is { } link && link.Id == id ? Task.FromResult(link) : Stat(id, ct);
        Task<Page<Item>> IStorage.Search(SearchQuery query, CancellationToken ct)
        {
            if (FailSearch) { throw new Unavailable(null); }
            return EmptyFirstPage && query.Cursor is null ? Task.FromResult(new Page<Item>([], "next")) : Search(query with { Cursor = null }, ct);
        }
    }

    private sealed class Broker(CapabilityConnection<IStorage>[] connections) : ICapabilityBroker
    {
        public Task<TCapability> Resolve<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class => throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityConnection<TCapability>>> ResolveAll<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class
            => Task.FromResult<IReadOnlyList<CapabilityConnection<TCapability>>>(connections.Select(item => new CapabilityConnection<TCapability>(item.ConnectionKey, item.Label, (TCapability)(object)item.Capability)).ToArray());
    }
}
