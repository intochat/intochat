using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

[Collection(nameof(RecordingApp))]
public sealed class DesktopAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> DesktopOperations => ScreenBaseline.Cases(typeof(DesktopApp), os => os.Register<DesktopRecordsNeuron>());

    [Theory, MemberData(nameof(DesktopOperations))]
    public async Task DesktopBaseline(ScreenOperationCase operation)
    {
        using var person = Edge.AsPerson();
        await ScreenBaseline.RunAsync(operation);
    }

    [Fact]
    public async Task DesktopScenariosPass()
    {
        using var person = Edge.AsPerson();
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(DesktopApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.desktop", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public void WorkspaceAndDesktopCarryTheFlutterTrees()
    {
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
        Assert.Equal("desktop", DesktopApp.Bundle.Name);
    }

    [Fact]
    public async Task OpenWithAppAndViewRoutesToThatView()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Equal(("apps/creator", "open", "trace/t-1"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task LegacyOpenByNameStillOpensTheWindow()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        var desktop = await os.Install<DesktopApp>();

        // "open:tasks" binds the name alone; no app neuron is registered, so a forward would throw.
        await desktop.Request("open", new DesktopOpen("tasks"));
        await desktop.Request("open", new DesktopOpen("creator", "trace/t-1"));

        Assert.Empty(RecordingApp.Requests);
        Assert.Null(desktop.App.RecordedError);
    }

    [Fact]
    public async Task CatalogAndShippedManifestsCarryArtCraftIconsAndCalculator()
    {
        foreach (var studio in IntoChat.OS.Modules.ArtCraft.ArtCraftCatalog.All)
        {
            var entry = Assert.Single(DesktopApp.Catalog, c => c.Id == studio.Id);
            Assert.Equal($"artcraft:{studio.Id}", entry.Icon);
        }

        Assert.Contains(DesktopApp.Catalog, c => c.Id == "calculator" && c.Icon == "calculator");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "photocraft" && c.Icon == "artcraft:photocraft");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "filmcraft" && c.Icon == "artcraft:filmcraft");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "markdraw");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "creator");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "files");
        Assert.Contains(DesktopApp.Catalog, c => c.Id == "settings");

        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(DesktopApp).Assembly]);
        var manifests = await ShippedManifests.Of(catalog);
        Assert.Equal("artcraft:photocraft", manifests.IconKey("photocraft"));
        Assert.Equal("artcraft:filmcraft", manifests.IconKey("filmcraft"));
        Assert.Equal("artcraft:vectorcraft", manifests.IconKey("vectorcraft"));
        Assert.Equal("artcraft:solidcraft", manifests.IconKey("solidcraft"));
        Assert.Equal("capability:calculator", manifests.IconKey("calculator"));
    }

    [Fact]
    public async Task DesktopDynamicallyShowsDeployedRuntimeUnitsAndOpensAndPinsThem()
    {
        FakeDeployedUnitsStudio.Aliases = ["helloworld", "btcalert"];
        await using var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        os.Register<FakeDeployedUnitsStudio>();
        var desktop = await os.Install<DesktopApp>();

        await desktop.Request("start", new DesktopStart());

        Assert.NotNull(desktop.App.State.DeployedUnits);
        Assert.Equal(["btcalert", "helloworld"], desktop.App.State.DeployedUnits!);
        var effective = desktop.App.EffectiveCatalog();
        Assert.Contains(effective, item => item.Id == "helloworld" && item.RuntimeMode == "synapse-unit");
        Assert.Contains(effective, item => item.Id == "btcalert" && item.RuntimeMode == "synapse-unit");

        var viewJson = System.Text.Json.JsonSerializer.Serialize(
            desktop.App.Render(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("dock-photocraft", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-filmcraft", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-markdraw", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-creator", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-unit-helloworld", viewJson, StringComparison.Ordinal);
        Assert.Contains("dock-unit-btcalert", viewJson, StringComparison.Ordinal);

        await desktop.Request("pin", new DesktopPinApp("helloworld", Pinned: true));
        Assert.Contains("helloworld", desktop.App.State.PinnedApps, StringComparer.OrdinalIgnoreCase);
    }

    [DigitalBrain.Neurons.Neuron("ino.synapses")]
    private sealed class FakeDeployedUnitsStudio : DigitalBrain.Neurons.Neuron, ISynapseStudio
    {
        public static string[] Aliases { get; set; } = [];

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult("ok"));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => throw new NotSupportedException();
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("ok"));
        public Task<DigitalBrain.Units.DeployedSource[]> List() => Task.FromResult(
            Aliases.Select(a => new DigitalBrain.Units.DeployedSource(a, "hash", [a], DigitalBrain.Units.DeployStatus.Deployed, null, DateTimeOffset.UtcNow, null)).ToArray());
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("none"));
    }
}
