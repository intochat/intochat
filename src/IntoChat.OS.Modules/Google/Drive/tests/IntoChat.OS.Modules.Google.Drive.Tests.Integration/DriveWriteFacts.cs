using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing.Conformance.Storage;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

public sealed class DriveWriteFacts(DriveHost host)
{
    private const string WriteScope = "https://www.googleapis.com/auth/drive";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_a_granted_write_scope_advertises_and_allows_mutations()
    {
        var read = await host.ConnectKitAsync(Ct);
        Assert.False((await read.Describe(Ct)).Flags.HasFlag(ItemFlags.Write));
        var root = Assert.Single(await read.Roots(Ct));
        await Assert.ThrowsAsync<Unsupported>(() => read.Write(WritePlan.CreateFolder(root.Id, "refused"), Ct));
        await Assert.ThrowsAsync<Unsupported>(() => read.OpenWrite(WritePlan.WriteFile(root.Id, "refused.txt"), Ct));
        var write = await host.ConnectKitAsync(Ct, WriteScope);
        Assert.True((await write.Describe(Ct)).Flags.HasFlag(ItemFlags.Write));
        Assert.True((await DriveItems.Find(write, "Notes", Ct)).Flags.HasFlag(ItemFlags.CreateFolder));
    }

    [Fact]
    public async Task Write_rename_copy_move_trash_and_permanent_delete_have_visible_results()
    {
        var (store, folder) = await Storage();
        await Succeeds(store, WritePlan.CreateFolder(folder, "target"));
        var target = await Named(store, folder, "target");
        await Succeeds(store, WritePlan.WriteFile(folder, "original.txt", "first"u8.ToArray()));
        var original = await Named(store, folder, "original.txt");
        Assert.Equal("first", await store.ReadText(original.Id, Ct));
        await Succeeds(store, WritePlan.Rename(original.Id, "renamed.txt"));
        Assert.Equal("renamed.txt", (await store.Stat(original.Id, Ct)).Name);
        await Succeeds(store, WritePlan.Copy(original.Id, target.Id));
        var copy = await Named(store, target.Id, "renamed.txt");
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("first", await store.ReadText(copy.Id, Ct));
        await Succeeds(store, WritePlan.Move(original.Id, target.Id, ConflictPolicy.Rename));
        Assert.Equal("renamed (1).txt", (await store.Stat(original.Id, Ct)).Name);
        Assert.DoesNotContain((await store.List(folder, new(), Ct)).Items, item => item.Id == original.Id);
        await Succeeds(store, WritePlan.Delete(original.Id));
        await Assert.ThrowsAsync<NotFound>(() => store.Stat(original.Id, Ct));
        await Succeeds(store, WritePlan.Delete(copy.Id, permanent: true));
        await Assert.ThrowsAsync<NotFound>(() => store.Stat(copy.Id, Ct));
    }

    [Fact]
    public async Task Conflicts_preserve_existing_content_unless_overwrite_is_explicit()
    {
        var (store, folder) = await Storage();
        await Succeeds(store, WritePlan.WriteFile(folder, "report.txt", "original"u8.ToArray()));
        var first = await Named(store, folder, "report.txt");
        var refused = await Result(store, WritePlan.WriteFile(folder, "report.txt", "wrong"u8.ToArray()));
        Assert.Equal("conflict", Assert.IsType<OperationResult.Failed>(refused).Code);
        Assert.Equal("original", await store.ReadText(first.Id, Ct));
        await Succeeds(store, WritePlan.WriteFile(folder, "report.txt", "second"u8.ToArray(), ConflictPolicy.Rename));
        Assert.Equal("second", await store.ReadText((await Named(store, folder, "report (1).txt")).Id, Ct));
        await Succeeds(store, WritePlan.WriteFile(folder, "report.txt", "replacement"u8.ToArray(), ConflictPolicy.Overwrite));
        Assert.Equal("replacement", await store.ReadText(first.Id, Ct));
    }

    [Fact]
    public async Task Dry_run_lists_a_plan_without_changing_storage()
    {
        var (store, folder) = await Storage();
        var before = (await store.List(folder, new(), Ct)).Items;
        Assert.IsType<OperationResult.Planned>(await Result(store, WritePlan.CreateFolder(folder, "dry") with { DryRun = true }));
        Assert.Equal(before, (await store.List(folder, new(), Ct)).Items);
    }

    [Fact]
    public async Task Folder_copy_preserves_nested_bytes_and_source_and_refuses_a_cycle()
    {
        var (store, folder) = await Storage();
        var deep = await Named(store, folder, "Deep");
        var refused = await Result(store, WritePlan.Copy(deep.Id, deep.Id));
        Assert.Equal("denied", Assert.IsType<OperationResult.Failed>(refused).Code);
        await Succeeds(store, WritePlan.CreateFolder(folder, "destination"));
        var destination = await Named(store, folder, "destination");
        await Succeeds(store, WritePlan.Copy(deep.Id, destination.Id));
        var copy = await Named(store, destination.Id, "Deep");
        Assert.NotEqual(deep.Id, copy.Id);
        foreach (var part in new[] { "1", "2", "3", "leaf.txt" }) { copy = await Named(store, copy.Id, part); }
        Assert.Equal("leaf", await store.ReadText(copy.Id, Ct));
        Assert.Equal(deep.Id, (await store.Stat(deep.Id, Ct)).Id);
    }

    [Fact]
    public async Task Refused_upload_completes_with_typed_error_and_preserves_target()
    {
        var (store, folder) = await Storage();
        await Succeeds(store, WritePlan.WriteFile(folder, "quota-refused.txt", "original"u8.ToArray()));
        var target = await Named(store, folder, "quota-refused.txt");
        host.KitDrive.RefuseUpload("quota-refused.txt", 403, "storageQuotaExceeded");
        var result = await Result(store, WritePlan.WriteFile(folder, target.Name, "replacement"u8.ToArray(), ConflictPolicy.Overwrite));
        Assert.Equal("quota-exceeded", Assert.IsType<OperationResult.Failed>(result).Code);
        Assert.Equal("original", await store.ReadText(target.Id, Ct));
    }

    [Fact]
    public async Task Streamed_upload_crosses_chunk_boundaries_and_rechecks_conflicts_at_commit()
    {
        var (store, folder) = await Storage();
        var bytes = Enumerable.Range(0, 600_000).Select(index => (byte)(index % 251)).ToArray();
        await using (var write = await store.OpenWrite(WritePlan.WriteFile(folder, "large.bin"), Ct))
        {
            await write.Content.WriteAsync(bytes, Ct);
            Assert.DoesNotContain((await store.List(folder, new(), Ct)).Items, item => item.Name == "large.bin");
            var id = await write.Commit(Ct);
            await using var read = await store.OpenRead(id, null, Ct);
            using var copy = new MemoryStream();
            await read.Content.CopyToAsync(copy, Ct);
            Assert.Equal(bytes, copy.ToArray());
        }
        await using var pending = await store.OpenWrite(WritePlan.WriteFile(folder, "raced.txt"), Ct);
        await pending.Content.WriteAsync("pending"u8.ToArray(), Ct);
        await Succeeds(store, WritePlan.WriteFile(folder, "raced.txt", "winner"u8.ToArray()));
        await Assert.ThrowsAsync<Conflict>(() => pending.Commit(Ct));
        Assert.Equal("winner", await store.ReadText((await Named(store, folder, "raced.txt")).Id, Ct));
    }

    [Fact]
    public async Task Upload_never_follows_a_session_location_on_another_origin()
    {
        var (store, folder) = await Storage();
        var name = "foreign-session-" + Guid.NewGuid().ToString("N");
        host.KitDrive.UploadLocation(name, new Uri("https://foreign.invalid/upload"));
        var result = await Result(store, WritePlan.WriteFile(folder, name, "private"u8.ToArray()));
        Assert.Equal("unavailable", Assert.IsType<OperationResult.Failed>(result).Code);
        Assert.DoesNotContain((await store.List(folder, new(), Ct)).Items, item => item.Name == name);
    }

    [Fact]
    public async Task Folder_copy_preflights_descendant_permissions_even_for_dry_run()
    {
        var (store, folder) = await Storage();
        var source = await Named(store, folder, "Deep");
        var name = "copy-refused-" + Guid.NewGuid().ToString("N");
        await Succeeds(store, WritePlan.WriteFile(source.Id, name, "keep"u8.ToArray()));
        host.KitDrive.RefuseCopy(name);
        var target = await Named(store, folder, "Notes");
        foreach (var dryRun in new[] { true, false })
        {
            var result = await Result(store, WritePlan.Copy(source.Id, target.Id) with { DryRun = dryRun });
            Assert.Equal("denied", Assert.IsType<OperationResult.Failed>(result).Code);
            Assert.DoesNotContain((await store.List(target.Id, new(), Ct)).Items, item => item.Name == source.Name);
        }
    }

    [Fact]
    public async Task Edit_permission_does_not_imply_rename_move_or_adding_children()
    {
        var (store, folder) = await Storage();
        host.KitDrive.RestrictMutations((await store.Stat(folder, Ct)).Name);
        var flags = (await store.Stat(folder, Ct)).Flags;
        Assert.False(flags.HasFlag(ItemFlags.Rename));
        Assert.False(flags.HasFlag(ItemFlags.Move));
        Assert.False(flags.HasFlag(ItemFlags.Write));
        Assert.False(flags.HasFlag(ItemFlags.CreateFolder));
        var result = await Result(store, WritePlan.WriteFile(folder, "refused.txt", "keep"u8.ToArray()));
        Assert.Equal("denied", Assert.IsType<OperationResult.Failed>(result).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_upload_response_is_typed_and_never_leaves_an_operation_running(bool streamed)
    {
        var (store, folder) = await Storage();
        var name = "malformed-" + Guid.NewGuid().ToString("N");
        host.KitDrive.MalformedUploadResponse(name);
        if (streamed)
        {
            await using var write = await store.OpenWrite(WritePlan.WriteFile(folder, name), Ct);
            await write.Content.WriteAsync("content"u8.ToArray(), Ct);
            await Assert.ThrowsAsync<Unavailable>(() => write.Commit(Ct));
        }
        else
        {
            var result = await Result(store, WritePlan.WriteFile(folder, name, "content"u8.ToArray()));
            Assert.Equal("unavailable", Assert.IsType<OperationResult.Failed>(result).Code);
        }
    }

    [Fact]
    public async Task Scoped_search_finds_descendants_across_pages_without_other_folders()
    {
        // This global Drive search must not scan the other facts' accumulating canonical trees.
        await using var stub = await DriveStub.StartAsync(Ct);
        await using var brain = await DriveBrain.StartAsync(stub, Ct);
        var store = await DriveHost.ConnectAsync(brain, Ct);
        var own = stub.Seed(StorageSeed.Canonical);
        _ = stub.Seed(StorageSeed.Canonical);
        var folder = await DriveItems.Find(store, own, Ct);
        var found = new List<Item>();
        var pages = 0;
        var emptyPages = 0;
        string? cursor = null;
        do
        {
            var page = await store.Search(new("leaf", Within: folder.Id, Cursor: cursor, PageSize: 1), Ct);
            pages++;
            if (page.Items.Count == 0) { emptyPages++; }
            found.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(2, pages);
        Assert.Equal(1, emptyPages);
        var match = Assert.Single(found);
        Assert.Equal("leaf.txt", match.Name);
        Assert.Contains(await store.Ancestors(match.Id, Ct), item => item.Id == folder.Id);
    }

    private async Task<(IStorage Storage, ItemId Folder)> Storage()
    {
        var storage = await host.ConnectKitAsync(Ct, WriteScope);
        var name = host.KitDrive.Seed(StorageSeed.Canonical);
        return (storage, (await DriveItems.Find(storage, name, Ct)).Id);
    }

    private static async Task<Item> Named(IStorage storage, ItemId folder, string name)
        => Assert.Single((await storage.List(folder, new(), Ct)).Items, item => item.Name == name);

    private async Task<OperationResult> Result(IStorage storage, WritePlan plan)
        => await host.Operations.WaitFor(await storage.Write(plan, Ct), Ct);

    private async Task Succeeds(IStorage storage, WritePlan plan)
        => Assert.IsType<OperationResult.Succeeded>(await Result(storage, plan));
}
