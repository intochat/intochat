using System.Text;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Authority.Tests.Integration.Connections;

// D-03: storage@1 over a connection that implements only the 0.6 ILegacyStorage, until Files moves in Phase 7.
public sealed class LegacyStorageAdapterFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RootsAndListMapEntriesToItemsWithTheLocationAsTheId()
    {
        var storage = new LegacyStorageAdapter(new Fake());

        var root = Assert.Single(await storage.Roots(Ct));
        Assert.Equal((new ItemId("r"), "Root", ItemKind.Folder), (root.Id, root.Name, root.Kind));
        var page = await storage.List(root.Id, new ListQuery(), Ct);
        Assert.Equal(["b", "a.txt", "c.txt"], page.Items.Select(item => item.Name));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListPagesClientSideByOffsetCursor()
    {
        var storage = new LegacyStorageAdapter(new Fake());

        var first = await storage.List(new ItemId("r"), new ListQuery(PageSize: 2), Ct);
        Assert.Equal(["b", "a.txt"], first.Items.Select(item => item.Name));
        Assert.Equal("2", first.NextCursor);
        var second = await storage.List(new ItemId("r"), new ListQuery(first.NextCursor, PageSize: 2), Ct);
        Assert.Equal(["c.txt"], second.Items.Select(item => item.Name));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task ReadReturnsTheUtf8BytesOfTheTextAndHonoursARange()
    {
        var storage = new LegacyStorageAdapter(new Fake());

        var whole = await storage.Read(new ItemId("r/a.txt"), null, Ct);
        Assert.Equal("héllo", Encoding.UTF8.GetString(whole.Bytes.Span));
        Assert.Equal(Encoding.UTF8.GetByteCount("héllo"), whole.TotalLength);
        var part = await storage.Read(new ItemId("r/a.txt"), new ByteRange(3, 2), Ct);
        Assert.Equal("ll", Encoding.UTF8.GetString(part.Bytes.Span));
        await using var stream = await storage.OpenRead(new ItemId("r/a.txt"), null, Ct);
        Assert.Equal(whole.Bytes.Length, stream.Length);
    }

    [Fact]
    public async Task DescribeIsReadOnlyWithUnknownQuotaAndWritesAreUnsupported()
    {
        var storage = new LegacyStorageAdapter(new Fake(), "This PC", "windows");

        var descriptor = await storage.Describe(Ct);
        Assert.Equal(("This PC", "windows", ItemFlags.Read), (descriptor.Label, descriptor.Kind, descriptor.Flags));
        Assert.Equal(QuotaState.Unknown, descriptor.Quota.State);
        Assert.Null(descriptor.Quota.Total);
        Assert.Equal("unsupported", (await Assert.ThrowsAsync<Unsupported>(() => storage.Write(WritePlan.CreateFolder(new ItemId("r"), "x"), Ct))).Code);
        await Assert.ThrowsAsync<Unsupported>(() => storage.OpenWrite(WritePlan.WriteFile(new ItemId("r"), "x"), Ct));
    }

    [Fact]
    public async Task StatAndAncestorsComeFromTheLegacyBreadcrumbs()
    {
        var storage = new LegacyStorageAdapter(new Fake());

        Assert.Equal("a.txt", (await storage.Stat(new ItemId("r/a.txt"), Ct)).Name);
        Assert.Equal(["Root"], (await storage.Ancestors(new ItemId("r/a.txt"), Ct)).Select(item => item.Name));
    }

    private sealed class Fake : ILegacyStorage
    {
        public Task<StorageEntry[]> Roots(CancellationToken cancellationToken = default) => Task.FromResult<StorageEntry[]>([new("r", "Root", true)]);

        public Task<StorageEntry[]> List(string location, CancellationToken cancellationToken = default)
            => Task.FromResult<StorageEntry[]>([new("r/c.txt", "c.txt", false), new("r/a.txt", "a.txt", false), new("r/b", "b", true)]);

        public Task<string> Read(string location, CancellationToken cancellationToken = default) => Task.FromResult("héllo");

        public Task<StorageEntry[]> Ancestors(string location, CancellationToken cancellationToken = default)
            => Task.FromResult<StorageEntry[]>([new("r", "Root", true), new(location, location[(location.LastIndexOf('/') + 1)..], false)]);
    }
}
