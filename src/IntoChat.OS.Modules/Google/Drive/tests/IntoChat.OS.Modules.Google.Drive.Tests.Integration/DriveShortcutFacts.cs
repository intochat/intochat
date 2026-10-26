using DigitalBrain.Capabilities.Storage;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// 05-09: shortcuts are links to their target, a folder shortcut lists its target (one hop), trashed items never list, and the
// quota is tri-state from about.get.
public sealed class DriveShortcutFacts(DriveHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShortcutsAreLinksAndAFolderShortcutListsItsTarget()
    {
        var (folder, folderShortcut, fileShortcut, target) = host.KitDrive.Shortcuts();
        var storage = await host.ConnectKitAsync(Ct);

        var items = (await storage.List(new ItemId(folder), new ListQuery(), Ct)).Items;
        Assert.DoesNotContain(items, item => item.Name == "trashed.txt");
        var toFolder = Assert.Single(items, item => item.Id.Value == folderShortcut);
        Assert.Equal((ItemKind.Link, (ItemId?)new ItemId(target)), (toFolder.Kind, toFolder.Target));
        Assert.Equal(ItemKind.Link, Assert.Single(items, item => item.Id.Value == fileShortcut).Kind);
        Assert.Equal(["inside.txt"], (await storage.List(toFolder.Id, new ListQuery(), Ct)).Items.Select(item => item.Name));
        Assert.Equal("unsupported", (await Assert.ThrowsAnyAsync<StorageException>(() => storage.List(new ItemId(fileShortcut), new ListQuery(), Ct))).Code);
    }

    [Fact]
    public async Task ItemFlagsStayReadOnlyUnderDriveReadonly()
    {
        var (folder, _, _, _) = host.KitDrive.Shortcuts();
        var storage = await host.ConnectKitAsync(Ct);

        Assert.All((await storage.List(new ItemId(folder), new ListQuery(), Ct)).Items, item => Assert.Equal(ItemFlags.Read, item.Flags));
        Assert.Equal(ItemFlags.Read | ItemFlags.Search, (await storage.Describe(Ct)).Flags);
    }

    [Fact]
    public async Task DescribeReportsAPooledKnownQuota()
    {
        var quota = (await (await host.ConnectKitAsync(Ct)).Describe(Ct)).Quota;
        Assert.Equal((QuotaState.Known, 1073741824L, 16106127360L, true), (quota.State, quota.Used, quota.Total, quota.Pooled));
    }

    [Fact]
    public async Task ASearchWithAQuoteIsEscaped()
    {
        var storage = await host.ConnectKitAsync(Ct);
        Assert.Empty((await storage.Search(new SearchQuery("it's"), Ct)).Items);
    }
}
