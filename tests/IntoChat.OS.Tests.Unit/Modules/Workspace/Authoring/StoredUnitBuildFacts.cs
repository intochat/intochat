using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Testing;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

// A unit build is stored as PE bytes and binds to contract assemblies by name. StoredBuild/btcalert.build.json is such a build:
// apps/btcalert/BtcAlert.cs compiled by SynapseCompiler(ReferenceSet.FromDefaultContext()) at commit 0aca5ca4a, when unit authoring
// was still part of the catalog assembly. The file holds the image (assembly and symbols), its hash and the source it was
// compiled from. It is never recompiled here: these facts say that what a brain stored before the authoring module left the
// catalog still loads, passes the deploy gate and runs after it. Do not regenerate the file; a new build proves nothing about old ones.
public sealed class StoredUnitBuildFacts
{
    private static readonly BrainId Owner = new("owner");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Stored(string SourceKey, string Hash, string AssemblyName, byte[] Assembly, byte[] Symbols, string Source);

    // The first scenario of apps/btcalert as it was when the build was stored: the price falls under the threshold on the first tick.
    private const string ScenarioJson = """
        {
          "name": "alerts-once-on-threshold-drop",
          "given": {
            "http": { "market": { "api.coinbase.com": { "data": { "amount": "84250.00", "currency": "USD" } } } },
            "notifications": { "notify": { "*": { "id": "notif-1", "deduplicated": false } } }
          },
          "when": ["tick 1m"],
          "then": ["signal btcalert.pricedropped", "state btcalert Armed = false", "state btcalert AlertsSent = 1"]
        }
        """;

    private static Stored Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "StoredBuild", "btcalert.build.json");
        return JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
    }

    private static BuildImage Image(Stored stored)
        => new(new BuildRef(stored.SourceKey, stored.Hash), stored.AssemblyName, stored.Assembly, stored.Symbols);

    [Fact]
    public void TheStoredBuildBindsToTheCatalogContractsByAssemblyNameAndToNoImplementation()
    {
        var stored = Read();
        using var pe = new PEReader(new MemoryStream(stored.Assembly, writable: false));
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();

        Assert.Contains("IntoChat.OS.Modules.Catalog.Contracts", references);
        Assert.DoesNotContain("IntoChat.OS.Modules.Catalog", references);
        Assert.DoesNotContain("IntoChat.OS.Modules.Authoring", references);
        // The contract the build names now lives in IntoChat.OS.Contracts and resolves via DigitalBrain.ReplacesAssembly.
        Assert.Equal("IntoChat.OS.Contracts", typeof(Notifications.INotifications).Assembly.GetName().Name);
        Assert.Equal("IntoChat.OS.Modules.Apps.Notifications.INotifications", typeof(Notifications.INotifications).FullName);
    }

    [Fact]
    public async Task TheStoredBuildLoadsDeploysWithoutACompileAndReactsToATick()
    {
        var stored = Read();
        var notifications = new CountingNotifications();
        string[] slots = ["market", "notify"];

        await using var os = TestOS.Local();
        using var person = os.Edge.Enter(os.Edge.Person(Owner.Value, Owner.Value, Owner));
        os.With<IHttpRead>("market", new FixedPrice());
        os.With<Notifications.INotifications>("notify", notifications);
        os.Confirmations.ApproveAll();

        // Load: the loader checks the image against the allowed references before it loads it, then scans its synapses.
        var image = Image(stored);
        string[] aliases;
        Dictionary<string, string> needs;
        using (var handle = os.Loader.Acquire(image))
        {
            aliases = [.. handle.Catalog.States.Keys];
            needs = aliases.ToDictionary(alias => alias, alias => handle.Catalog.NeedsOf(alias).Hash(), StringComparer.Ordinal);
        }
        Assert.Equal(["btcalert"], aliases);

        // Deploy: the stored build goes to the deployment neuron as the deployer hands it over, with no compiler in between.
        var build = new StoredBuild(image, new Dictionary<string, string>(StringComparer.Ordinal) { ["BtcAlert.cs"] = stored.Source }, null, aliases, needs);
        var bindings = aliases.ToDictionary(
            alias => alias,
            alias => (IReadOnlyDictionary<string, string>)slots.ToDictionary(slot => slot, slot => slot, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var outcome = await os.Get<IUnitDeployment>(new NeuronId(Owner, "synapse.deployment", stored.SourceKey)).Propose(build, bindings, Ct);
        await os.Idle();
        Assert.Equal(DeployStatus.Deployed, outcome.Status);
        Assert.DoesNotContain(outcome.Installs.Values, install => install.Status is InstallStatus.Refused or InstallStatus.Denied);

        // Run: one minute on, the unit read the price through its http slot, sent one alert through the catalog's notifications
        // contract and disarmed itself.
        os.Clock.Advance(TimeSpan.FromMinutes(1));
        await os.Idle();
        var state = await os.Get<IStateReader>(new NeuronId(Owner, "btcalert", "default")).Read("btcalert");
        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        Assert.False(Property(state, "Armed").GetBoolean());
        Assert.Equal(1, Property(state, "AlertsSent").GetInt32());
        Assert.Equal(1, notifications.Sent);
        Assert.Empty(os.Failures);
    }

    [Fact]
    public async Task TheSourceOfTheStoredBuildPassesTheDeployGateOfTheAuthoringModule()
    {
        var stored = Read();
        var scenario = JsonSerializer.Deserialize<Scenario>(ScenarioJson, JsonSerializerOptions.Web)!;
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BtcAlert.cs"] = stored.Source,
            [UnitFiles.Scenarios] = JsonSerializer.Serialize(new[] { scenario }, JsonSerializerOptions.Web),
        };
        IUnitVerifier verifier = new UnitVerifier();
        Assert.Equal("IntoChat.OS.Modules.Workspace", verifier.GetType().Assembly.GetName().Name);

        var check = await verifier.Verify(stored.SourceKey, files, [scenario], Ct);
        Assert.True(check.Passed, string.Join("\n", check.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}").Concat(check.Failures)));

        var transcript = await verifier.Transcript(stored.SourceKey, files, null, Ct);
        Assert.Contains("✓ alerts-once-on-threshold-drop", transcript, StringComparison.Ordinal);
        Assert.Contains("btcalert.pricedropped by ", transcript, StringComparison.Ordinal);
    }

    private sealed class FixedPrice : IHttpRead
    {
        private const string Body = """{"data":{"amount":"84250.00","currency":"USD"}}""";

        public Task<JsonElement> GetJson(string url, CancellationToken ct) => Task.FromResult(JsonElement.Parse(Body));

        public Task<string> GetText(string url, CancellationToken ct) => Task.FromResult(Body);
    }

    private sealed class CountingNotifications : Notifications.INotifications
    {
        private int _sent;

        public int Sent => _sent;

        public Task<JsonElement> Notify(string title, string body, string priority, CancellationToken ct)
        {
            Interlocked.Increment(ref _sent);
            return Task.FromResult(JsonElement.Parse("""{"id":"notif-1","deduplicated":false}"""));
        }
    }

    private static JsonElement Property(JsonElement record, string name)
        => record.EnumerateObject().First(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
