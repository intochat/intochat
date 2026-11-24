using System.Text;
using DigitalBrain.Testing;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Storage;

// A one-time move of selected records between two brain stores (the cloud leaving a shared cluster for its own): back up first,
// copy and verify, never overwrite what the target already holds, and remove from the source only what was verified in the target.
public sealed class BrainRecordMoveFacts : IDisposable
{
    private const string Visitor = "neuron.host/b1/lobby.visitor/v1";
    private const string Credential = "circle-server.machine-credential/b1/m1";
    private const string Directory = "neuron.host/b1/identity.directory/main";

    private static readonly IConfiguration Keys = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [RuntimeOptions.Default.MasterKeyKey] = "test-master-key-0123456789",
    }).Build();

    private static readonly BrainRecordRule[] CloudRules = [new(Neuron: "lobby.visitor"), new(Grain: "circle-server.machine-credential/")];

    private readonly TempDirectory root = new("brain-record-move-");

    private string Source => Path.Combine(root.Path, "os-home");
    private string Target => Path.Combine(root.Path, "cloud-home");
    private string Backup => Path.Combine(root.Path, "backup");
    private BrainMovePlan Plan => new(Source, Target, Backup, ["b1"], CloudRules);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => root.Dispose();

    private static Task<BrainMoveLine[]> Run(BrainMovePlan plan, BrainMovePhase phase) => BrainRecordMove.Run(plan, phase, Keys, RuntimeOptions.Default, Ct);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes("{\"v\":\"" + text + "\"}");

    private static async Task Write(string home, string grain, string text, string brain = "b1")
    {
        await using var store = new BrainStore(home);
        var log = await store.Log(brain);
        await log.AppendEntry(grain, "state", log.Record(grain, "state")?.Version, BrainRecordChange.Write, Bytes(text), "", [], Ct);
    }

    private static async Task<string?> Read(string home, string grain, string brain = "b1")
    {
        await using var store = new BrainStore(home);
        if (!System.IO.Directory.Exists(store.DirectoryOf(brain))) { return null; }
        var record = (await store.Log(brain)).Record(grain, "state");
        return record is null ? null : Encoding.UTF8.GetString(record.Bytes.Span);
    }

    private async Task Seed()
    {
        await Write(Source, Visitor, "visitor");
        await Write(Source, Credential, "credential");
        await Write(Source, Directory, "directory");
    }

    [Fact]
    public async Task ThePlanListsWhatTheRulesSelect_AndWritesNothingToTheTarget()
    {
        await Seed();

        var line = Assert.Single(await Run(Plan, BrainMovePhase.Plan));

        Assert.Equal(2, line.Selected);
        Assert.False(System.IO.Directory.Exists(Target));
    }

    [Fact]
    public async Task NothingIsCopiedBeforeAVerifiedBackup_AndNothingRemovedBeforeAVerifiedCopy()
    {
        await Seed();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Plan, BrainMovePhase.Copy));
        await Run(Plan, BrainMovePhase.Backup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Plan, BrainMovePhase.RemoveSource));
        Assert.Null(await Read(Target, Visitor));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("visitor")), await Read(Source, Visitor));
    }

    [Fact]
    public async Task TheBackupIsAVerifiedByteCopyOfTheStore()
    {
        await Seed();

        await Run(Plan, BrainMovePhase.Backup);

        await using var store = new BrainStore(Source);
        var folder = store.DirectoryOf("b1");
        var copy = Path.Combine(Backup, "brains", Path.GetFileName(folder));
        foreach (var file in System.IO.Directory.EnumerateFiles(folder).Where(file => Path.GetExtension(file) is ".log" or ".snapshot" or ".key"))
        {
            Assert.Equal(await File.ReadAllBytesAsync(file, Ct), await File.ReadAllBytesAsync(Path.Combine(copy, Path.GetFileName(file)), Ct));
        }
    }

    [Fact]
    public async Task TheCopyMovesOnlyTheSelectedRecords_VerifiesThem_AndLeavesTheSourceWhole()
    {
        await Seed();
        await Run(Plan, BrainMovePhase.Backup);

        var line = Assert.Single(await Run(Plan, BrainMovePhase.Copy));

        Assert.Equal((2, 2, 0), (line.Selected, line.Copied, line.AlreadyThere));
        Assert.Empty(line.Conflicts);
        Assert.Equal(Encoding.UTF8.GetString(Bytes("visitor")), await Read(Target, Visitor));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("credential")), await Read(Target, Credential));
        Assert.Null(await Read(Target, Directory));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("visitor")), await Read(Source, Visitor));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("directory")), await Read(Source, Directory));
    }

    [Fact]
    public async Task EveryPhaseCanRunAgain()
    {
        await Seed();
        await Run(Plan, BrainMovePhase.Backup);
        await Run(Plan, BrainMovePhase.Copy);

        // The verified backup is kept, not taken again over a store the copy has opened since.
        Assert.True(Assert.Single(await Run(Plan, BrainMovePhase.Backup)).AlreadyThere > 0);
        var again = Assert.Single(await Run(Plan, BrainMovePhase.Copy));

        Assert.Equal((0, 2), (again.Copied, again.AlreadyThere));
    }

    [Fact]
    public async Task ARecordTheTargetAlreadyHoldsDifferently_IsNeverOverwritten_AndBlocksRemoval()
    {
        await Seed();
        await Write(Target, Visitor, "the cloud's newer visitor");
        await Run(Plan, BrainMovePhase.Backup);

        var line = Assert.Single(await Run(Plan, BrainMovePhase.Copy));

        Assert.Equal([Visitor + " state"], line.Conflicts);
        Assert.Equal(Encoding.UTF8.GetString(Bytes("the cloud's newer visitor")), await Read(Target, Visitor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Plan, BrainMovePhase.RemoveSource));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("visitor")), await Read(Source, Visitor));
    }

    [Fact]
    public async Task RemovalClearsOnlyVerifiedCopies_AndKeepsWhatChangedSinceTheCopy()
    {
        await Seed();
        await Run(Plan, BrainMovePhase.Backup);
        await Run(Plan, BrainMovePhase.Copy);
        await Write(Source, Credential, "credential changed after the copy");

        var line = Assert.Single(await Run(Plan, BrainMovePhase.RemoveSource));

        Assert.Equal(1, line.Removed);
        Assert.Equal([Credential + " state"], line.Conflicts);
        Assert.Null(await Read(Source, Visitor));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("credential changed after the copy")), await Read(Source, Credential));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("directory")), await Read(Source, Directory));
        Assert.Equal(Encoding.UTF8.GetString(Bytes("visitor")), await Read(Target, Visitor));
    }

    [Fact]
    public async Task ABrainWithNoStoreIsSkipped()
    {
        var plan = Plan with { Brains = ["absent"] };

        await Run(plan, BrainMovePhase.Backup);
        var line = Assert.Single(await Run(plan, BrainMovePhase.Copy));

        Assert.Equal(0, line.Selected);
    }

    [Fact]
    public async Task OverlappingFoldersOrAnEmptyPlanAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Plan with { TargetHome = Path.Combine(Source, "inner") }, BrainMovePhase.Plan));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Plan with { Backup = Path.Combine(Source, "backup") }, BrainMovePhase.Plan));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Plan with { Rules = [] }, BrainMovePhase.Plan));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(Plan with { Rules = [new BrainRecordRule()] }, BrainMovePhase.Plan));
    }
}
