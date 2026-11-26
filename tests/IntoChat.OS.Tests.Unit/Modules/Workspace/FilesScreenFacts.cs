using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class FilesStorageFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void QuotaTextIsTriStateAndNeverZero()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal("120 GB free of 512 GB", QuotaText.For(Quota.Known(392 * gb, 512 * gb), "local"));
        Assert.Equal("8.2 GB of 15 GB", QuotaText.For(Quota.Known((long)(8.2m * gb), 15 * gb), "google-drive"));
        Assert.Equal("1.1 GB used", QuotaText.For(Quota.Unlimited((long)(1.1m * gb)), "cloud"));
        Assert.Equal(FilesCopy.SpaceUnknown, QuotaText.For(Quota.Unknown, "local"));
        Assert.Equal(FilesCopy.SpaceUnknown, QuotaText.For(Quota.Known(0, 0), "local"));
    }

    [Fact]
    public async Task UnavailableStorageShowsTheWarningAndTryAgain()
    {
        var down = new ThrowingStorage();
        var app = Harness.App(down);
        await app.Open(new(), Ct);
        var row = FilesScreen.Compose(app.Snapshot, 1440, false).Side;
        Assert.Contains(FilesCopy.Unavailable, ViewReads.Texts(row), StringComparer.Ordinal);
        app.ShowUnavailable(app.Snapshot.Storages[0].Connection);
        var main = FilesScreen.Compose(app.Snapshot, 1440, false).Main;
        Assert.Contains(FilesCopy.UnavailableTitle("A"), ViewReads.Texts(main), StringComparer.Ordinal);
        Assert.Contains(FilesCopy.TryAgain, ViewReads.Buttons(main), StringComparer.Ordinal);
        Assert.Equal(FilesCopy.UnavailableTitle("A"), FilesScreen.Compose(app.Snapshot, 1440, false).Files.Error);
    }

    [Fact]
    public async Task NoStorageConnectedOffersSettings()
    {
        var app = new FilesApp(new Harness.Broker([]), new("owner"), new());
        await app.Open(new(), Ct);
        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Contains(FilesCopy.NoStorageHeading, ViewReads.Texts(screen.Main), StringComparer.Ordinal);
        Assert.Contains(FilesCopy.NoStorageBody, ViewReads.Texts(screen.Main), StringComparer.Ordinal);
        Assert.Contains(FilesCopy.OpenSettings, ViewReads.Buttons(screen.Main), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ListAndSearchGoThroughTheBroker()
    {
        var storage = new InMemoryStorage("disk", kind: "local");
        var folder = storage.AddFolder(storage.Root, "docs");
        storage.AddFile(folder, "note.txt", "hi"u8.ToArray());
        var seen = new Counting(storage);
        var app = Harness.App(seen);
        await app.Browse(new("a", folder), Ct);
        await app.Search(new("note", Everywhere: true), Ct);
        Assert.Contains("List", seen.Calls);
        Assert.Contains("Search", seen.Calls);
    }
}

public sealed class FilesBrowseFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FoldersComeFirstAndAFullPageOffersLoadMore()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "docs");
        storage.AddFolder(folder, "zeta");
        storage.AddFile(folder, "alpha.txt", [1]);
        for (var n = 0; n < 100; n++) { storage.AddFile(folder, $"n{n:D3}.txt", [1]); }
        var app = Harness.App(storage);
        await app.Browse(new("a", folder), Ct);
        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Equal("zeta", screen.Files.Items[0].Label);
        Assert.Equal("folder", screen.Files.Items[0].Kind);
        Assert.Equal(100, screen.Files.Items.Length);
        Assert.Contains(FilesCopy.LoadMore, ViewReads.Buttons(screen.Main), StringComparer.Ordinal);
        Assert.Contains(folder, app.Snapshot.Breadcrumbs.Select(item => item.Item.Id));
    }

    [Fact]
    public async Task FilterIsACaseInsensitiveSubstringAndClearIsOffered()
    {
        var storage = new InMemoryStorage();
        storage.AddFile(storage.Root, "Holiday.jpg", [1]);
        storage.AddFile(storage.Root, "notes.txt", [1]);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        app.Filter("HOLI");
        var matched = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Equal("Holiday.jpg", Assert.Single(matched.Files.Items).Label);
        app.Filter("zz");
        var none = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Empty(none.Files.Items);
        Assert.Equal(FilesCopy.NoMatchHeading, none.Files.EmptyLabel);
        Assert.Contains(FilesCopy.ClearFilter, ViewReads.Buttons(none.Main), StringComparer.Ordinal);
    }

    [Fact]
    public async Task EmptyFolderUsesTheEmptyLabel()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "docs");
        var app = Harness.App(storage);
        await app.Browse(new("a", folder), Ct);
        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Equal(FilesCopy.EmptyHeading, screen.Files.EmptyLabel);
        Assert.Contains(FilesCopy.EmptyBody, ViewReads.Texts(screen.Main), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ListFailureIsAnErrorNotAnEmptyFolder()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "docs");
        var app = Harness.App(new Counting(storage) { FailList = true });
        await app.Browse(new("a", folder), Ct);
        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Equal(FilesCopy.ListFailed, screen.Files.Error);
        Assert.Null(screen.Files.EmptyLabel);
        Assert.Empty(screen.Files.Items);
    }

    [Fact]
    public async Task LoadingShowsProgressAndANullCollectionError()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "docs");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = Harness.App(new Counting(storage) { ListGate = gate.Task });
        var pending = app.Browse(new("a", folder), Ct);
        Assert.True(app.Snapshot.Loading);
        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Null(screen.Files.Error);
        Assert.Contains(FilesCopy.Loading, ViewReads.Texts(screen.Main), StringComparer.Ordinal);
        gate.SetResult();
        await pending;
        Assert.False(app.Snapshot.Loading);
        Assert.Null(FilesScreen.Compose(app.Snapshot, 1440, false).Files.Error);
    }

    [Fact]
    public async Task ColumnHeadersToggleSortAndKeepFoldersFirst()
    {
        var clock = new ForwardClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var storage = new InMemoryStorage(time: clock);
        storage.AddFolder(storage.Root, "dir");
        storage.AddFile(storage.Root, "b.txt", new byte[2]);
        clock.Advance(TimeSpan.FromDays(1));
        storage.AddFile(storage.Root, "a.txt", new byte[9]);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        var headers = ViewReads.Buttons(FilesScreen.Compose(app.Snapshot, 1440, false).Main);
        Assert.Contains(FilesCopy.NameHeader, headers);
        Assert.Contains(FilesCopy.SizeHeader, headers);
        Assert.Contains(FilesCopy.ModifiedHeader, headers);
        app.SortBy("name");
        Assert.Equal(["dir", "b.txt", "a.txt"], FilesScreen.Ordered(app.Snapshot).Select(item => item.Item.Name));
        app.SortBy("name");
        Assert.Equal(["dir", "a.txt", "b.txt"], FilesScreen.Ordered(app.Snapshot).Select(item => item.Item.Name));
        app.SortBy("size");
        Assert.Equal("dir", FilesScreen.Ordered(app.Snapshot).First().Item.Name);
        Assert.Equal(["b.txt", "a.txt"], FilesScreen.Ordered(app.Snapshot).Skip(1).Select(item => item.Item.Name));
        app.SortBy("modified");
        Assert.Equal("dir", FilesScreen.Ordered(app.Snapshot).First().Item.Name);
        Assert.Equal("a.txt", FilesScreen.Ordered(app.Snapshot).Last().Item.Name);
        app.SortBy("modified");
        Assert.Equal("a.txt", FilesScreen.Ordered(app.Snapshot).Skip(1).First().Item.Name);
    }
}

public sealed class FilesSearchFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchGroupsEachStorageAndDoesNotActivateTheHeader()
    {
        var drive = new InMemoryStorage("Google Drive");
        var disk = new InMemoryStorage("This computer");
        drive.AddFile(drive.Root, "holiday.txt", [1]);
        disk.AddFile(disk.Root, "holiday.txt", [1]);
        var app = Harness.App(("drive", "Google Drive", drive), ("disk", "This computer", disk));
        await app.Search(new("holiday", Everywhere: true), Ct);
        var items = FilesScreen.Compose(app.Snapshot, 1440, false).Files.Items;
        Assert.Equal("group", items[0].Kind);
        Assert.False(items[0].CanActivate);
        Assert.Equal("Google Drive", items[0].Label);
        Assert.Equal("holiday.txt", items[1].Label);
        Assert.Equal("group", items[2].Kind);
        Assert.False(items[2].CanActivate);
        Assert.Equal("This computer", items[2].Label);
        Assert.Equal("holiday.txt", items[3].Label);
    }

    [Fact]
    public async Task ATimedOutStorageIsNamedAndTheOthersStay()
    {
        var drive = new InMemoryStorage("Google Drive");
        drive.AddFile(drive.Root, "holiday.txt", [1]);
        var slow = new Counting(new InMemoryStorage("This computer")) { SearchHangs = true };
        var app = Harness.App(("drive", "Google Drive", drive), ("disk", "This computer", slow));
        app.SearchLimit = TimeSpan.FromMilliseconds(50);
        await app.Search(new("holiday", Everywhere: true), Ct);
        Assert.Equal(FilesCopy.SearchMissed, app.Snapshot.StatusTitle);
        Assert.Contains("This computer", app.Snapshot.StatusBody, StringComparison.Ordinal);
        Assert.Contains(FilesCopy.GroupSilent("This computer"), FilesScreen.Compose(app.Snapshot, 1440, false).Files.Items.Select(item => item.Label));
        Assert.Contains("holiday.txt", FilesScreen.Compose(app.Snapshot, 1440, false).Files.Items.Select(item => item.Label));
    }

    [Fact]
    public async Task ABoundedSearchShowsTheFooter()
    {
        var storage = new Counting(new InMemoryStorage()) { BoundSearch = true };
        storage.Inner.AddFile(storage.Inner.Root, "holiday.txt", [1]);
        var app = Harness.App(storage);
        await app.Search(new("holiday", Everywhere: true), Ct);
        Assert.Contains(FilesCopy.Bounded(1), ViewReads.Texts(FilesScreen.Compose(app.Snapshot, 1440, false).Main), StringComparer.Ordinal);
    }

    [Fact]
    public async Task AnEmptyGroupAndAnEmptySearchUseTheFixedLines()
    {
        var drive = new InMemoryStorage("Google Drive");
        var disk = new InMemoryStorage("This computer");
        drive.AddFile(drive.Root, "holiday.txt", [1]);
        var app = Harness.App(("drive", "Google Drive", drive), ("disk", "This computer", disk));
        await app.Search(new("holiday", Everywhere: true), Ct);
        Assert.Contains(FilesCopy.NoMatchesIn("This computer"), FilesScreen.Compose(app.Snapshot, 1440, false).Files.Items.Select(item => item.Label));
        await app.Search(new("missing", Everywhere: true), Ct);
        Assert.Equal(FilesCopy.NothingNamed("missing"), app.Snapshot.StatusTitle);
    }

    [Fact]
    public async Task OpeningAResultSelectsItAndBackReturnsToThePreviousFolder()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "trips");
        var file = storage.AddFile(folder, "holiday.txt", "x"u8.ToArray());
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Search(new("holiday", Everywhere: true), Ct);
        var result = app.Snapshot.Items.Single(item => item.Item.Id == file);
        await app.OpenResult(result, Ct);
        Assert.Equal(folder, app.Snapshot.Folder);
        Assert.Equal(file, app.Snapshot.Selected!.Item.Id);
        Assert.False(app.Snapshot.Searching);
        await app.BackToFolder(Ct);
        Assert.Equal(storage.Root, app.Snapshot.Folder);
    }
}

public sealed class FilesPreviewFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NarrowPreviewFillsTheWidthAndBackKeepsTheSelection()
    {
        var storage = new InMemoryStorage();
        var file = storage.AddFile(storage.Root, "note.txt", "hello"u8.ToArray());
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(file, Ct))), Ct);
        var open = FilesScreen.Compose(app.Snapshot, 390, previewOpen: true);
        Assert.Contains(FilesCopy.BackToFiles, ViewReads.Buttons(open.Main), StringComparer.Ordinal);
        Assert.DoesNotContain(FilesCopy.NameHeader, ViewReads.Buttons(open.Main), StringComparer.Ordinal);
        var back = FilesScreen.Compose(app.Snapshot, 390, previewOpen: false);
        Assert.Contains(back.Files.Items, item => item.Label == "note.txt");
        Assert.Equal(file, app.Snapshot.Selected!.Item.Id);
    }

    [Fact]
    public async Task TextPreviewStopsAt256Kb()
    {
        var storage = new InMemoryStorage();
        var small = storage.AddFile(storage.Root, "small.txt", Enumerable.Repeat((byte)'a', 10 * 1024).ToArray());
        var large = storage.AddFile(storage.Root, "large.txt", Enumerable.Repeat((byte)'b', 300 * 1024).ToArray());
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(small, Ct))), Ct);
        Assert.Equal(10 * 1024, app.Snapshot.Preview!.Length);
        Assert.Null(app.Snapshot.PreviewNote);
        await app.Select(new(new("a", await storage.Stat(large, Ct))), Ct);
        Assert.Equal(256 * 1024, app.Snapshot.Preview!.Length);
        Assert.Equal(FilesCopy.PreviewLimited, app.Snapshot.PreviewNote);
    }

    [Fact]
    public async Task ImagesUseTheContentUrlUntilTheyAreTooLarge()
    {
        var storage = new InMemoryStorage();
        var picture = storage.AddFile(storage.Root, "pic.png", [1, 2, 3]);
        var huge = storage.AddFile(storage.Root, "huge.png", [1]);
        var sized = new Counting(storage);
        sized.Sizes[huge] = 11L * 1024 * 1024;
        sized.Sizes[picture] = 2L * 1024 * 1024;
        var app = Harness.App(sized);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await sized.Stat(picture, Ct))), Ct);
        // The hosted content route, the connection key in its one route form (never the raw key with its slashes).
        Assert.Equal(DigitalBrain.Runtime.Http.Storage.StorageContentEndpoints.ContentPath("owner", "a", picture.Value), app.Snapshot.PreviewUrl);
        Assert.StartsWith("/brains/owner/storage/" + DigitalBrain.Runtime.Http.Storage.StorageContentEndpoints.Segment("a") + "/items/", app.Snapshot.PreviewUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("Read", sized.Calls);
        await app.Select(new(new("a", await sized.Stat(huge, Ct))), Ct);
        Assert.Equal(FilesCopy.PreviewTooLarge, app.Snapshot.Preview);
        Assert.Null(app.Snapshot.PreviewUrl);
    }

    [Fact]
    public async Task AScriptInsideAnInstalledAppFolderShowsTheAppCard()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "notes");
        var script = storage.AddFile(folder, "Program.cs", "return;"u8.ToArray());
        var other = storage.AddFile(storage.Root, "loose.dat", [1, 0, 2]);
        var app = Harness.App(storage);
        app.InstalledApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "notes" };
        await app.Browse(new("a", folder), Ct);
        await app.Select(new(new("a", await storage.Stat(script, Ct))), Ct);
        var card = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Equal("notes", app.Snapshot.PreviewApp);
        Assert.Contains(FilesCopy.OpenApp, ViewReads.Buttons(card.Main), StringComparer.Ordinal);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(other, Ct))), Ct);
        Assert.Equal(FilesCopy.PreviewUnavailable, app.Snapshot.Preview);
    }

    [Fact]
    public async Task InteractiveBreadcrumbsQuickViewAndAskInoHandoffExposeNeuronUri()
    {
        var storage = new InMemoryStorage();
        var folder = storage.AddFolder(storage.Root, "projects");
        var file = storage.AddFile(folder, "Context.md", "# Context"u8.ToArray());
        var app = Harness.App(storage);
        await app.Browse(new("a", folder), Ct);
        await app.Select(new(new("a", await storage.Stat(file, Ct))), Ct);

        var prompt = app.AskIno();
        Assert.Contains("neuron://owner/a.files/projects/context.md", prompt, StringComparison.Ordinal);
        Assert.Equal(prompt, app.Snapshot.InoHandoffPrompt);

        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        var buttons = ViewReads.Buttons(screen.Main);
        var texts = ViewReads.Texts(screen.Main);
        Assert.Contains(FilesCopy.CopyNeuronUri + " (" + FilesCopy.CopyNeuronUriShortcut + ")", buttons, StringComparer.Ordinal);
        Assert.Contains(FilesCopy.CopyDisplayPath, buttons, StringComparer.Ordinal);
        Assert.Contains(FilesCopy.AskInoAboutFile, buttons, StringComparer.Ordinal);
        Assert.Contains(FilesCopy.QuickViewHeading, texts, StringComparer.Ordinal);
        Assert.Contains(prompt, texts, StringComparer.Ordinal);

        var keys = ViewReads.Keys(screen.Main);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("crumb-root", keys, StringComparer.Ordinal);
        Assert.Contains("crumb-storage", keys, StringComparer.Ordinal);
        Assert.Contains("crumb-0", keys, StringComparer.Ordinal);
        Assert.Contains("item-open-0", keys, StringComparer.Ordinal);

        var fields = Walk.Nodes(screen.Main).Where(n => Walk.Kind(n) == "field").ToArray();
        Assert.NotEmpty(fields);
        Assert.All(fields, field => Assert.StartsWith("owner/apps/files/", Walk.Str(field, "name"), StringComparison.Ordinal));
    }
}

public sealed class FilesMediaPreviewFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AudioFileProducesAudioPreviewNodeAndStreamUrl()
    {
        var storage = new InMemoryStorage();
        var file = storage.AddFile(storage.Root, "podcast.mp3", [1, 2, 3]);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(file, Ct))), Ct);

        Assert.NotNull(app.Snapshot.PreviewUrl);
        Assert.Null(app.Snapshot.Preview);
        Assert.EndsWith("/content", app.Snapshot.PreviewUrl, StringComparison.Ordinal);

        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        var texts = ViewReads.Texts(screen.Main);
        Assert.Contains("AUDIO preview", texts, StringComparer.Ordinal);
        Assert.Contains(app.Snapshot.PreviewUrl, texts, StringComparer.Ordinal);
    }

    [Fact]
    public async Task VideoFileProducesVideoPreviewNodeAndStreamUrl()
    {
        var storage = new InMemoryStorage();
        var file = storage.AddFile(storage.Root, "screencast.mp4", [1, 2, 3]);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(file, Ct))), Ct);

        Assert.NotNull(app.Snapshot.PreviewUrl);
        Assert.Null(app.Snapshot.Preview);

        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        var texts = ViewReads.Texts(screen.Main);
        Assert.Contains("VIDEO preview", texts, StringComparer.Ordinal);
        Assert.Contains(app.Snapshot.PreviewUrl, texts, StringComparer.Ordinal);
    }

    [Fact]
    public async Task PdfFileProducesPdfPreviewNodeAndStreamUrl()
    {
        var storage = new InMemoryStorage();
        var file = storage.AddFile(storage.Root, "report.pdf", [1, 2, 3]);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(file, Ct))), Ct);

        Assert.NotNull(app.Snapshot.PreviewUrl);
        Assert.Null(app.Snapshot.Preview);

        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        var texts = ViewReads.Texts(screen.Main);
        Assert.Contains("PDF preview", texts, StringComparer.Ordinal);
        Assert.Contains(app.Snapshot.PreviewUrl, texts, StringComparer.Ordinal);
    }

    [Fact]
    public async Task SvgAndAvifImagesProduceImagePreview()
    {
        var storage = new InMemoryStorage();
        var svg = storage.AddFile(storage.Root, "icon.svg", "<svg></svg>"u8);
        var app = Harness.App(storage);
        await app.Browse(new("a", storage.Root), Ct);
        await app.Select(new(new("a", await storage.Stat(svg, Ct))), Ct);

        Assert.NotNull(app.Snapshot.PreviewUrl);
        Assert.Null(app.Snapshot.Preview);

        var screen = FilesScreen.Compose(app.Snapshot, 1440, false);
        Assert.Contains(
            Walk.Nodes(screen.Main),
            n => Walk.Kind(n) == "image"
                && Walk.Str(n, "fit") == "Contain"
                && Walk.Str(n, "url") == app.Snapshot.PreviewUrl);
    }
}

file static class Harness
{
    public static FilesApp App(IStorage storage) => new(new Broker([new("a", "A", storage)]), new("owner"), new());

    public static FilesApp App(params (string Key, string Label, IStorage Storage)[] connections)
        => new(new Broker(connections.Select(item => new CapabilityConnection<IStorage>(item.Key, item.Label, item.Storage)).ToArray()), new("owner"), new());

    internal sealed class Broker(CapabilityConnection<IStorage>[] connections) : ICapabilityBroker
    {
        public Task<TCapability> Resolve<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class => throw new NotSupportedException();
        public Task<IReadOnlyList<CapabilityConnection<TCapability>>> ResolveAll<TCapability>(BrainId brain, CancellationToken ct) where TCapability : class
            => Task.FromResult<IReadOnlyList<CapabilityConnection<TCapability>>>(connections.Select(item => new CapabilityConnection<TCapability>(item.ConnectionKey, item.Label, (TCapability)(object)item.Capability)).ToArray());
    }
}

file static class Walk
{
    // Every object of the node's wire JSON, depth first.
    public static IEnumerable<System.Text.Json.JsonElement> Nodes(DigitalBrain.UI.Node node)
        => Objects(System.Text.Json.JsonSerializer.SerializeToElement(node, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(DigitalBrain.UI.Node))));

    private static IEnumerable<System.Text.Json.JsonElement> Objects(System.Text.Json.JsonElement element)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            yield return element;
            foreach (var child in element.EnumerateObject().Where(p => p.Name is not ("emit" or "args")).SelectMany(p => Objects(p.Value))) { yield return child; }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray().SelectMany(Objects)) { yield return child; }
        }
    }

    public static string? Kind(System.Text.Json.JsonElement node) => node.TryGetProperty("kind", out var kind) ? kind.GetString() : null;

    public static string? Str(System.Text.Json.JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
}

file static class ViewReads
{
    public static string[] Texts(DigitalBrain.UI.Node node)
        => [.. Walk.Nodes(node).Where(item => Walk.Kind(item) != "button").SelectMany(item => new[] { Walk.Str(item, "value"), Walk.Str(item, "title"), Walk.Str(item, "body"), Walk.Str(item, "label") }).OfType<string>()];
    public static string[] Buttons(DigitalBrain.UI.Node node) => [.. Walk.Nodes(node).Where(item => Walk.Kind(item) == "button").Select(item => Walk.Str(item, "label")!)];
    public static string[] Keys(DigitalBrain.UI.Node node) => [.. Walk.Nodes(node).Select(item => Walk.Str(item, "key")).OfType<string>()];
}

file sealed class ForwardClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = now;
    public void Advance(TimeSpan by) => Now += by;
    public override DateTimeOffset GetUtcNow() => Now;
}

file sealed class ThrowingStorage : IStorage
{
    public Task<StorageDescriptor> Describe(CancellationToken ct = default) => throw new Unavailable(null);
    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => throw new Unavailable(null);
    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<Item> Stat(ItemId id, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => throw new Unavailable(null);
    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => throw new Unavailable(null);
}

file sealed class Counting(InMemoryStorage inner) : IStorage
{
    public InMemoryStorage Inner { get; } = inner;
    public List<string> Calls { get; } = [];
    public bool FailList { get; init; }
    public bool SearchHangs { get; init; }
    public bool BoundSearch { get; init; }
    public Task? ListGate { get; init; }
    public Dictionary<ItemId, long> Sizes { get; } = [];

    public Task<StorageDescriptor> Describe(CancellationToken ct = default) { Calls.Add(nameof(Describe)); return Inner.Describe(ct); }
    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) { Calls.Add(nameof(Roots)); return Inner.Roots(ct); }
    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        Calls.Add(nameof(List));
        if (ListGate is { } gate) { await gate.WaitAsync(ct); }
        if (FailList) { throw new Unavailable(folder); }
        return Sized(await Inner.List(folder, query, ct));
    }
    public async Task<Item> Stat(ItemId id, CancellationToken ct = default) { Calls.Add(nameof(Stat)); return Sized(await Inner.Stat(id, ct)); }
    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) { Calls.Add(nameof(Read)); return Inner.Read(id, range, ct); }
    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => Inner.Write(plan, ct);
    public async Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
    {
        Calls.Add(nameof(Search));
        if (SearchHangs) { await Task.Delay(Timeout.Infinite, ct); }
        var page = await Inner.Search(query, ct);
        return BoundSearch ? page with { NextCursor = "more" } : page;
    }
    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => Inner.Ancestors(id, ct);
    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => Inner.OpenRead(item, range, ct);
    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => Inner.OpenWrite(plan, ct);

    private Page<Item> Sized(Page<Item> page) => page with { Items = page.Items.Select(Sized).ToArray() };
    private Item Sized(Item item) => Sizes.TryGetValue(item.Id, out var size) ? item with { Size = size } : item;
}
