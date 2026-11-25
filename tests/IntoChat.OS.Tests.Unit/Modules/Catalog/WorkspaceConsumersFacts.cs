using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The neurons that read and write the brain's workspace reach it through the broker by its connection key, address a
// file by its path (folders kept), and never pick a file or a storage by a name match.
public sealed class WorkspaceConsumersFacts : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BrainId Brain = new("alice");

    private readonly string _workspace = PathConfinement.Normalize(Directory.CreateTempSubdirectory("consumers-workspace-").FullName);
    private readonly string _documents = PathConfinement.Normalize(Directory.CreateTempSubdirectory("consumers-documents-").FullName);
    private readonly string _decoy = PathConfinement.Normalize(Directory.CreateTempSubdirectory("consumers-decoy-").FullName);

    [Fact]
    public async Task InosDataQueryReadsTheCsvAtTheNamedPathNotAFileOfTheSameName()
    {
        Seed(Path.Combine(_workspace, "sales", "customer-arr.csv"), "Account,City,Owner,ARR\nHavbris AS,Bergen,Marte L.,148000\nFjordkraft,Oslo,Henrik S.,112000\nNordic Wind,Stavanger,Marte L.,94500\n");
        Seed(Path.Combine(_workspace, "customer-arr.csv"), "Account,City,Owner,ARR\nElsewhere,Nowhere,Someone Else,1\n");
        await using var os = Host();
        os.Register<InoDataNeuron>();

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var data = os.Get<IData>(new NeuronId(Brain, "ino.data", IData.DefaultKey));
            var result = await data.Query("SELECT Account, Owner, ARR FROM /work/sales/customer-arr.csv GROUP BY Owner");

            Assert.True(result.Ok, result.Text);
            var chart = Assert.Single((await data.Read()).Charts);
            Assert.Equal("Marte L.", chart.ChartPoints![0].Label);
            Assert.Equal(242500, chart.ChartPoints[0].Value);
        }
    }

    [Fact]
    public async Task InosFilesFindAndOpenByPathAndNeverByAMatchingName()
    {
        Seed(Path.Combine(_workspace, "notes", "plan.md"), "# The plan\nShip the workspace.");
        Seed(Path.Combine(_documents, "budget.txt"), "Budget 2027");
        await using var os = Host();
        os.Register<InoFilesNeuron>();

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var files = os.Get<IFiles>(new NeuronId(Brain, "ino.files", IFiles.DefaultKey));

            var found = await files.Find("plan");
            Assert.Contains("notes/plan.md", found.Text, StringComparison.Ordinal);
            Assert.Contains("Ship the workspace.", (await files.Summarize("notes/plan.md")).Text, StringComparison.Ordinal);
            Assert.StartsWith("Opened plan.md", (await files.Open("/work/notes/plan.md")).Text, StringComparison.Ordinal);
            // The old service fell back to any file of that name in any mount.
            Assert.StartsWith("No file found", (await files.Open("plan.md")).Text, StringComparison.Ordinal);

            // A file of another storage is opened by the reference files_find gave, not by its name.
            var budget = await files.Find("budget");
            var reference = budget.Text.Split('·').Select(part => part.Trim().TrimEnd(')')).First(part => part.StartsWith("storage:", StringComparison.Ordinal));
            Assert.Contains("Budget 2027", (await files.Summarize(reference)).Text, StringComparison.Ordinal);
            Assert.StartsWith("No file found", (await files.Open("budget.txt")).Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task InosFilesWriteGoesToTheLocalDiskByItsKeyNotToAConnectionWhoseKeyMerelyContainsIt()
    {
        await using var os = Host();
        os.Register<InoFilesNeuron>();
        os.Confirmations.ApproveAll();

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", Brain)))
        {
            var files = os.Get<IFiles>(new NeuronId(Brain, "ino.files", IFiles.DefaultKey));
            var asked = await files.Write("todo.txt", "write the report");
            Assert.Contains("approve", asked.Text, StringComparison.OrdinalIgnoreCase);
        }

        for (var attempt = 0; attempt < 100 && !File.Exists(Path.Combine(_documents, "todo.txt")); attempt++) { await Task.Delay(50, Ct); }
        Assert.Equal("write the report", await File.ReadAllTextAsync(Path.Combine(_documents, "todo.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(_decoy, "todo.txt")));
    }

    [Fact]
    public async Task TheArtCraftEngineRefusesACallWithNoCaller()
    {
        await using var os = Host();
        os.Register<ArtCraftEngineNeuron>();
        var engine = os.Get<IArtCraftEngine>(new NeuronId(Brain, "apps.artcraft-engine", "apps/artcraft-engine"));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => engine.Execute("artcraft", "open", null, null, "local"));
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _workspace, _documents, _decoy })
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    // The decoy is listed first and its key contains "local-disk": the old Write took the first such connection.
    private TestOS Host()
    {
        var os = TestOS.Local();
        os.With<IStorage>("alice/old-local-disk-backup/main", new LocalDiskStorage([new LocalRoot("decoy", _decoy)], new InMemoryOperations(), coordinator: new LocalDiskCoordinator()));
        os.With<IStorage>(BrainWorkspaceFiles.ConnectionKey(Brain), new LocalDiskStorage([new LocalRoot(LocalCloudNeuron.RootId, _workspace)], new InMemoryOperations(), coordinator: new LocalDiskCoordinator(),
            shape: new LocalDiskShape("Workspace", "local-cloud", null, WorkspaceNames.IsHiddenOrSecretSegment)));
        os.With<IStorage>(InoFilesNeuron.LocalDiskKey(Brain), new LocalDiskStorage([new LocalRoot("documents", _documents)], new InMemoryOperations(), coordinator: new LocalDiskCoordinator()));
        return os;
    }

    private static void Seed(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }
}
