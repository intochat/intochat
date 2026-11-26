using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The workspace-files neuron (IWorkspaceFiles, workspace.files): the brain's workspace by path for a caller of this brain,
// and the studio screens that open, save and export their documents through it as a declared slot.
public sealed class WorkspaceFilesFacts : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _workspace = PathConfinement.Normalize(Directory.CreateTempSubdirectory("workspace-files-").FullName);

    [Fact]
    public async Task ThePersonOfTheBrainWritesReadsListsAndFindsFilesByPath()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "reports"));
        await using var os = Host("alice");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice")));
        var files = os.Get<IWorkspaceFiles>(WorkspaceFilesDefaults.Id(new BrainId("alice")));

        var written = await files.Write("/work/reports/q4.csv", "Stage,Amount\nWon,10\n"u8.ToArray());

        Assert.Equal(new WorkspaceFile("/work/reports/q4.csv", "q4.csv", false, 20, written.Modified), written);
        Assert.Equal("Stage,Amount\nWon,10\n", await File.ReadAllTextAsync(Path.Combine(_workspace, "reports", "q4.csv"), Ct));
        Assert.Equal("Stage,Amount\nWon,10\n", Encoding.UTF8.GetString((await files.Read("reports/q4.csv"))!));
        Assert.Equal("/work/reports/q4.csv", Assert.Single(await files.List("reports")).Path);
        Assert.Contains(await files.List(""), file => file is { Path: "/work/reports", IsFolder: true });
        Assert.Equal("q4.csv", (await files.Find("/work/reports/q4.csv"))?.Name);
        Assert.Null(await files.Find("reports/q5.csv"));
        Assert.Null(await files.Read("reports/q5.csv"));
        Assert.Empty(await files.List("no-such-folder"));
    }

    [Fact]
    public async Task APathOutsideTheWorkspaceAndAWriteLargerThanACallAreRefused()
    {
        await using var os = Host("alice");
        using var person = os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice")));
        var files = os.Get<IWorkspaceFiles>(WorkspaceFilesDefaults.Id(new BrainId("alice")));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => files.Read("/local/secret.txt"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => files.Write("../outside.txt", [1]));
        // A write carries its bytes as a call argument: the runtime refuses one larger than a call takes.
        await Assert.ThrowsAnyAsync<DigitalBrain.Invocation.BindingException>(() => files.Write("big.bin", new byte[2 * 1024 * 1024]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace));
    }

    [Fact]
    public async Task ACallWithNoCallerOrFromAnotherBrainIsRefused()
    {
        await using var os = Host("alice");
        var files = os.Get<IWorkspaceFiles>(WorkspaceFilesDefaults.Id(new BrainId("alice")));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => files.List(""));
        using (os.Edge.Enter(os.Edge.Person("mallory", "mallory", new BrainId("mallory"))))
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => files.Read("notes.txt"));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => files.Write("notes.txt", [1]));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(_workspace));
    }

    [Fact]
    public void TheStudiosAreGrantedTheWorkspaceFiles()
    {
        foreach (var app in new[] { "data", "markdraw", "ino", "photocraft", "artcraft" })
        {
            Assert.Contains(ShippedGrants.For("ada", app), grant => grant.Target == "workspace.files/ada/*");
        }
        Assert.DoesNotContain(ShippedGrants.For("ada", "calculator"), grant => grant.Target.StartsWith("workspace.files/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkdrawSavesAndExportsToTheWorkspaceAndOpensWhatIsThere()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "diagrams"));
        await using var os = Host("local");
        var markdraw = await os.Install<MarkdrawApp>();

        await markdraw.Request("open", new MarkdrawOpen("/work/architecture.markdraw"));
        await markdraw.Request("save", new MarkdrawSave("/work/architecture.markdraw"));
        Assert.StartsWith("Saved /work/architecture.markdraw to the workspace", markdraw.App.State.Status, StringComparison.Ordinal);
        Assert.Equal(markdraw.App.State.Content, await File.ReadAllTextAsync(Path.Combine(_workspace, "architecture.markdraw"), Ct));

        await markdraw.Request("export", new MarkdrawExport("svg", "/work/diagrams/architecture.svg"));
        Assert.StartsWith("<svg", await File.ReadAllTextAsync(Path.Combine(_workspace, "diagrams", "architecture.svg"), Ct), StringComparison.Ordinal);
        Assert.Contains("/work/diagrams/architecture.svg", markdraw.App.State.WorkspaceDrawings);

        // A folder that is not there: the scene stays in the canvas and the status says it was not written.
        await markdraw.Request("export", new MarkdrawExport("excalidraw", "/work/missing/architecture.excalidraw"));
        Assert.Contains("not written to /work/missing/architecture.excalidraw", markdraw.App.State.Status, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(_workspace, "mine.markdraw"), MarkdrawApp.DefaultPipelineScene, Ct);
        await markdraw.Request("open", new MarkdrawOpen("/work/mine.markdraw"));
        Assert.Equal(MarkdrawApp.DefaultPipelineScene, markdraw.App.State.Content);
    }

    [Fact]
    public async Task DataStudioQueriesAWorkspaceCsvAndExportsItsTableAndDiagramThere()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "sales"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "sales", "deals.csv"), "Deal,Stage,Amount\nAlpha,Won,100\nBeta,Won,50\nGamma,Lost,10\n", Ct);
        await using var os = Host("local");
        var data = await os.Install<DataStudioApp>();

        await data.Request("query", new DataStudioQuery("SELECT Stage, SUM(Amount) FROM /work/sales/deals.csv GROUP BY Stage"));
        Assert.Contains(data.App.State.Rows, row => row.Contains("Won") && row.Contains("150"));

        await data.Request("export-csv", new DataStudioExportCsv("/work/sales/by-stage.csv"));
        Assert.Contains("Won", await File.ReadAllTextAsync(Path.Combine(_workspace, "sales", "by-stage.csv"), Ct), StringComparison.Ordinal);
        Assert.Equal("/work/sales/by-stage.csv", data.App.State.LastExportPath);

        await data.Request("to-markdraw", new DataStudioToMarkdraw("/work/sales/lineage.markdraw"));
        Assert.Contains("```markdraw", await File.ReadAllTextAsync(Path.Combine(_workspace, "sales", "lineage.markdraw"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InosQueryDbReadsAWorkspaceCsv()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "pipeline.csv"), "Owner,ARR\nMarte,100\nHenrik,40\nMarte,20\n", Ct);
        await using var os = Host("local");
        var ino = await os.Install<InoApp>();

        await ino.Request("query-db", new InoQueryDb("SELECT Owner, SUM(ARR) FROM /work/pipeline.csv GROUP BY Owner"));

        Assert.DoesNotContain("Could not run the query", ino.App.State.Status ?? "", StringComparison.Ordinal);
        Assert.Contains("pipeline.csv", System.Text.Json.JsonSerializer.Serialize(ino.App.State), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArtCraftStudioOpensTheWorkspaceFileItWorksOn()
    {
        await File.WriteAllBytesAsync(Path.Combine(_workspace, "holiday.png"), [137, 80, 78, 71], Ct);
        await using var os = Host("local");
        var photo = await os.Install<PhotoCraftApp>();

        await photo.Request("open", new PhotoCraftStudioOpen());

        Assert.Equal("/work/holiday.png", photo.App.State.ActiveDocument);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    private TestOS Host(string brain)
    {
        var os = TestOS.Local();
        os.RegisterCatalog();
        os.With<IStorage>(BrainWorkspaceFiles.ConnectionKey(new BrainId(brain)), new ConfinedDiskStorage([new LocalRoot(LocalCloudNeuron.RootId, _workspace)], new InMemoryOperations(), coordinator: new ConfinedDiskCoordinator(),
            shape: ConfinedDiskShape.Workspace(null, WorkspaceNames.IsHiddenOrSecretSegment)));
        return os;
    }
}
