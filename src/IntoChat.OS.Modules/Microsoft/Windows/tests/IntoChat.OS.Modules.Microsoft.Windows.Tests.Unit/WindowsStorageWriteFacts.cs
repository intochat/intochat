using System.Diagnostics;
using System.Text.Json;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit;

public sealed class WindowsStorageWriteFacts : IDisposable
{
    private readonly TestHome _home = TestHome.Create();
    private readonly InMemoryOperations _operations = new();
    private readonly WindowsStorageCoordinator _coordinator = new();
    private readonly List<string> _roots;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Root => Path.Combine(_home.Path, "allowed");
    private WindowsStorage Storage => new(_roots, operations: _operations, coordinator: _coordinator);
    private ItemId RootId => WindowsStorage.IdOf(Root);

    public WindowsStorageWriteFacts()
    {
        Directory.CreateDirectory(Root);
        _roots = [Root];
    }

    public void Dispose() { _coordinator.Dispose(); _home.Dispose(); }

    private ItemId Id(string name) => WindowsStorage.IdOf(Path.Combine(Root, name));

    private async Task<OperationResult> Run(WritePlan plan)
    {
        var id = await Storage.Write(plan, Ct);
        return await _operations.WaitFor(id, Ct);
    }

    private async Task Succeeds(WritePlan plan) => Assert.IsType<OperationResult.Succeeded>(await Run(plan));

    [Fact]
    public async Task CreatesFoldersAndWritesFilesAsCompletedOperations()
    {
        await Succeeds(WritePlan.CreateFolder(RootId, "notes"));
        await Succeeds(WritePlan.WriteFile(Id("notes"), "one.txt", "first"u8.ToArray()));
        Assert.Equal("first", await Storage.ReadText(Id(Path.Combine("notes", "one.txt")), Ct));
        Assert.True((await Storage.Describe(Ct)).Flags.HasFlag(ItemFlags.CreateFolder | ItemFlags.Write));
        Assert.Equal(2, _operations.Events.Count(entry => entry.Stage == "done"));
    }

    [Fact]
    public async Task ConflictsFailOrRenameAndReplaceOnlyWhenExplicit()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "report.txt"), "original", Ct);
        Assert.Equal("conflict", Assert.IsType<OperationResult.Failed>(await Run(WritePlan.WriteFile(RootId, "report.txt", "new"u8.ToArray()))).Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Root, "report.txt"), Ct));
        await Succeeds(WritePlan.WriteFile(RootId, "report.txt", "second"u8.ToArray(), ConflictPolicy.Rename));
        Assert.Equal("second", await Storage.ReadText(Id("report (1).txt"), Ct));
        await Succeeds(WritePlan.WriteFile(RootId, "report.txt", "replacement"u8.ToArray(), ConflictPolicy.Overwrite));
        Assert.Equal("replacement", await Storage.ReadText(Id("report.txt"), Ct));
    }

    [Fact]
    public async Task CopyMoveAndRenamePreserveTheWholeTree()
    {
        Directory.CreateDirectory(Path.Combine(Root, "source", "nested"));
        Directory.CreateDirectory(Path.Combine(Root, "target"));
        await File.WriteAllTextAsync(Path.Combine(Root, "source", "nested", "item.txt"), "content", Ct);
        await Succeeds(WritePlan.Copy(Id("source"), Id("target")));
        Assert.True(File.Exists(Path.Combine(Root, "source", "nested", "item.txt")));
        await Succeeds(WritePlan.Rename(Id(Path.Combine("target", "source")), "renamed"));
        await Succeeds(WritePlan.Move(Id(Path.Combine("target", "renamed")), RootId));
        Assert.False(Directory.Exists(Path.Combine(Root, "target", "renamed")));
        Assert.Equal("content", await Storage.ReadText(Id(Path.Combine("renamed", "nested", "item.txt")), Ct));
    }

    [Fact]
    public async Task DeleteDefaultsToRecoverableTrashAndPermanentIsExplicit()
    {
        var original = Path.Combine(Root, "keep.txt");
        await File.WriteAllTextAsync(original, "recoverable", Ct);
        await Succeeds(WritePlan.Delete(Id("keep.txt")));
        Assert.False(File.Exists(original));
        var trash = Path.Combine(Root, ".intochat-trash");
        Assert.Contains(Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories), path => File.ReadAllText(path) == "recoverable");
        var receipt = Assert.Single(Directory.EnumerateFiles(trash, "receipt.json", SearchOption.AllDirectories));
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, Ct));
        Assert.Equal(original, metadata.RootElement.GetProperty("OriginalPath").GetString());
        Assert.Empty((await Storage.List(RootId, new(), Ct)).Items);
        await Assert.ThrowsAsync<Denied>(() => Storage.Stat(WindowsStorage.IdOf(receipt), Ct));
        await File.WriteAllTextAsync(Path.Combine(Root, "remove.txt"), "permanent", Ct);
        await Succeeds(WritePlan.Delete(Id("remove.txt"), permanent: true));
        Assert.DoesNotContain(Directory.EnumerateFiles(trash, "*", SearchOption.AllDirectories), path => File.ReadAllText(path) == "permanent");
    }

    [Fact]
    public async Task AnUnmarkedTrashNameCollisionLeavesUserFilesUntouched()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".intochat-trash"));
        await File.WriteAllTextAsync(Path.Combine(Root, ".intochat-trash", "mine.txt"), "mine", Ct);
        await File.WriteAllTextAsync(Path.Combine(Root, "keep.txt"), "keep", Ct);
        Assert.IsType<OperationResult.Failed>(await Run(WritePlan.Delete(Id("keep.txt"))));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Root, "keep.txt"), Ct));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(Root, ".intochat-trash", "mine.txt"), Ct));
    }

    [Fact]
    public async Task DryRunsAndCancelledRequestsDoNotCreateAnything()
    {
        Assert.IsType<OperationResult.Planned>(await Run(WritePlan.CreateFolder(RootId, "planned") with { DryRun = true }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Storage.Write(WritePlan.CreateFolder(RootId, "cancelled"), cancelled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public async Task StreamWritesStayInvisibleUntilCommitAndAbortOrDisposeDiscardThem()
    {
        await using (var writer = await Storage.OpenWrite(WritePlan.WriteFile(RootId, "committed.txt"), Ct))
        {
            await writer.Content.WriteAsync("committed"u8.ToArray(), Ct);
            Assert.Empty((await Storage.List(RootId, new(), Ct)).Items);
            Assert.Empty((await Storage.Search(new("committed"), Ct)).Items);
            Assert.Equal(Id("committed.txt"), await writer.Commit(Ct));
        }
        foreach (var abort in new[] { true, false })
        {
            await using var writer = await Storage.OpenWrite(WritePlan.WriteFile(RootId, "discard.txt"), Ct);
            await writer.Content.WriteAsync("discard"u8.ToArray(), Ct);
            if (abort) { await writer.Abort(Ct); }
        }
        Assert.Equal("committed", await Storage.ReadText(Id("committed.txt"), Ct));
        Assert.Single((await Storage.List(RootId, new(), Ct)).Items);
    }

    [Fact]
    public async Task CommitRechecksConflictsAndDiscardsItsStagedBytesOnFailure()
    {
        await using var writer = await Storage.OpenWrite(WritePlan.WriteFile(RootId, "race.txt"), Ct);
        await writer.Content.WriteAsync("unpublished"u8.ToArray(), Ct);
        await File.WriteAllTextAsync(Path.Combine(Root, "race.txt"), "other writer", Ct);
        await Assert.ThrowsAsync<Conflict>(() => writer.Commit(Ct));
        Assert.Equal("other writer", await Storage.ReadText(Id("race.txt"), Ct));
        Assert.DoesNotContain(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories), path => File.ReadAllText(path) == "unpublished");
    }

    [Fact]
    public async Task RevokingARootAfterOpeningAStreamPreventsCommit()
    {
        var storage = Storage;
        await using var writer = await storage.OpenWrite(WritePlan.WriteFile(RootId, "revoked.txt"), Ct);
        await writer.Content.WriteAsync("private"u8.ToArray(), Ct);
        _roots.Clear();
        await Assert.ThrowsAsync<Denied>(() => writer.Commit(Ct));
        Assert.False(File.Exists(Path.Combine(Root, "revoked.txt")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub\\escape.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    public async Task InvalidNamesCannotEscapeOrAddressWindowsDevices(string name)
    {
        await Assert.ThrowsAsync<Denied>(() => Storage.OpenWrite(WritePlan.WriteFile(RootId, name), Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public async Task RootsCannotBeDeletedRenamedOrMovedIntoTheirOwnChildren()
    {
        Directory.CreateDirectory(Path.Combine(Root, "child"));
        foreach (var plan in new[] { WritePlan.Delete(RootId, true), WritePlan.Rename(RootId, "renamed"), WritePlan.Move(RootId, Id("child")), WritePlan.Copy(RootId, Id("child")) })
        { Assert.IsType<OperationResult.Failed>(await Run(plan)); }
        Assert.True(Directory.Exists(Path.Combine(Root, "child")));
    }

    [Fact]
    public async Task CaseOnlyRenamePreservesContentAndChangesTheDisplayedName()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "mixed.txt"), "case", Ct);
        await Succeeds(WritePlan.Rename(Id("mixed.txt"), "MIXED.txt"));
        Assert.Equal("MIXED.txt", Assert.Single((await Storage.List(RootId, new(), Ct)).Items).Name);
        Assert.Equal("case", await Storage.ReadText(Id("MIXED.txt"), Ct));
    }

    [Fact]
    public async Task CopyCannotOverwriteItsOwnAncestorAndDestroyItsSource()
    {
        Directory.CreateDirectory(Path.Combine(Root, "folder", "folder"));
        await File.WriteAllTextAsync(Path.Combine(Root, "folder", "folder", "keep.txt"), "keep", Ct);
        Assert.Equal("denied", Assert.IsType<OperationResult.Failed>(await Run(WritePlan.Copy(Id(Path.Combine("folder", "folder")), RootId, ConflictPolicy.Overwrite))).Code);
        Assert.Equal("keep", await Storage.ReadText(Id(Path.Combine("folder", "folder", "keep.txt")), Ct));
    }

    [Fact]
    public async Task CancelledCommitDiscardsBytesAndKeepsThePreviousVersion()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "keep.txt"), "original", Ct);
        await using var writer = await Storage.OpenWrite(WritePlan.WriteFile(RootId, "keep.txt", conflict: ConflictPolicy.Overwrite), Ct);
        await writer.Content.WriteAsync("cancelled"u8.ToArray(), Ct);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.Commit(cancelled.Token));
        Assert.Equal("original", await Storage.ReadText(Id("keep.txt"), Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(Root, ".intochat-trash", ".staging")));
    }

    [Fact]
    public async Task ExistingNamesCannotAddressAlternateDataStreams()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "item.txt"), "visible", Ct);
        await Assert.ThrowsAsync<Denied>(() => Storage.OpenRead(Id("item.txt:hidden"), null, Ct));
    }

    [Fact]
    public async Task JunctionsCannotExposeReadWriteOrDeleteOutsideTheAllowedRoot()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_home.Path, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside", Ct);
        var link = Path.Combine(Root, "shortcut");
        await Junction(link, outside);
        try
        {
            await Assert.ThrowsAsync<Denied>(() => Storage.Read(Id(Path.Combine("shortcut", "keep.txt")), null, Ct));
            await Assert.ThrowsAsync<Denied>(() => Storage.List(Id("shortcut"), new(), Ct));
            await Assert.ThrowsAsync<Denied>(() => Storage.OpenWrite(WritePlan.WriteFile(Id("shortcut"), "injected.txt"), Ct));
            Assert.Equal("denied", Assert.IsType<OperationResult.Failed>(await Run(WritePlan.Delete(Id("shortcut"), permanent: true))).Code);
            Assert.Empty((await Storage.List(RootId, new(), Ct)).Items);
            Assert.Empty((await Storage.Search(new("keep"), Ct)).Items);
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt"), Ct));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task ReplacingTheDestinationWithAJunctionAfterOpenCannotRedirectCommit()
    {
        var target = Directory.CreateDirectory(Path.Combine(Root, "target")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_home.Path, "outside")).FullName;
        await using var writer = await Storage.OpenWrite(WritePlan.WriteFile(Id("target"), "injected.txt"), Ct);
        await writer.Content.WriteAsync("hidden"u8.ToArray(), Ct);
        Directory.Move(target, Path.Combine(Root, "original"));
        await Junction(target, outside);
        try
        {
            await Assert.ThrowsAsync<Denied>(() => writer.Commit(Ct));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(target); }
    }

    private static async Task Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create the test junction.");
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task SearchHasNoSilentTenThousandEntryCutoffAndPagesWithoutDuplicates()
    {
        var many = Directory.CreateDirectory(Path.Combine(Root, "many")).FullName;
        for (var i = 0; i < 10_005; i++) { File.WriteAllText(Path.Combine(many, $"match-{i:D5}.txt"), ""); }
        var ids = new HashSet<ItemId>();
        string? cursor = null;
        do
        {
            var page = await Storage.Search(new("match-", Within: Id("many"), Cursor: cursor, PageSize: 1000), Ct);
            foreach (var item in page.Items) { Assert.True(ids.Add(item.Id)); }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        Assert.Equal(10_005, ids.Count);
    }
}
