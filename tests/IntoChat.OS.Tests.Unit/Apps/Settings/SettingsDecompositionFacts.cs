using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

[CollectionDefinition(nameof(SettingsDecompositionFacts), DisableParallelization = true)]
public sealed class SettingsDecompositionCollection;

[Collection(nameof(SettingsDecompositionFacts))]
public sealed class SettingsDecompositionFacts
{
    [Fact]
    public async Task LazySectionProjection_DoesNotQueryInactiveDomainGrainsUntilNavigated()
    {
        CountingApprovalsNeuron.Reset();
        CountingSynapseStudioNeuron.Reset();
        CountingModelCatalogNeuron.Reset();
        CountingConnectionCatalogNeuron.Reset();
        CountingComputeNeuron.Reset();
        CountingDeviceLinkNeuron.Reset();

        var vault = new CountingSecretVault();
        var services = new ServiceCollection()
            .AddSingleton<ISecretVault>(vault)
            .BuildServiceProvider();

        try
        {
            var brain = new BrainId("local");
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
            await using var os = TestOS.Local(o => o.Services = services)
                .Register<UserSettingsNeuron>()
                .Register<CountingApprovalsNeuron>()
                .Register<CountingSynapseStudioNeuron>()
                .Register<CountingModelCatalogNeuron>()
                .Register<CountingConnectionCatalogNeuron>()
                .Register<CountingComputeNeuron>()
                .Register<CountingDeviceLinkNeuron>();

            var settings = await os.Install<SettingsApp>();

            // 1. Opening on "general" only reads lightweight UserSettings shell state — ZERO inactive domain fan-out.
            await settings.Request("open", new SettingsOpen("general"));
            Assert.Equal("general", settings.App.State.Section);
            Assert.Equal(0, CountingApprovalsNeuron.PendingCalls);
            Assert.Equal(0, CountingSynapseStudioNeuron.ListCalls);
            Assert.Equal(0, CountingModelCatalogNeuron.ListCalls);
            Assert.Equal(0, CountingConnectionCatalogNeuron.ReadCalls);
            Assert.Equal(0, CountingComputeNeuron.ReadCalls);
            Assert.Equal(0, CountingDeviceLinkNeuron.StatusCalls);
            Assert.Equal(0, vault.StatusCalls);

            // 2. Navigating to "notifications" queries ONLY Approvals.
            await settings.Request("navigate", new SettingsNavigate("notifications"));
            Assert.Equal(1, CountingApprovalsNeuron.PendingCalls);
            Assert.Equal(0, CountingSynapseStudioNeuron.ListCalls);
            Assert.Equal(0, CountingModelCatalogNeuron.ListCalls);
            Assert.Equal(0, CountingConnectionCatalogNeuron.ReadCalls);
            Assert.Equal(0, CountingComputeNeuron.ReadCalls);
            Assert.Equal(0, CountingDeviceLinkNeuron.StatusCalls);
            Assert.Equal(0, vault.StatusCalls);

            // 3. Navigating to "apps" queries SynapseStudio.
            await settings.Request("navigate", new SettingsNavigate("apps"));
            Assert.Equal(1, CountingSynapseStudioNeuron.ListCalls);
            Assert.Equal(0, CountingModelCatalogNeuron.ListCalls);

            // 4. Navigating to "models" queries ModelCatalog.
            await settings.Request("navigate", new SettingsNavigate("models"));
            Assert.Equal(1, CountingModelCatalogNeuron.ListCalls);
            Assert.Equal(0, CountingConnectionCatalogNeuron.ReadCalls);

            // 5. Navigating to "connections" queries ConnectionCatalog.
            await settings.Request("navigate", new SettingsNavigate("connections"));
            Assert.Equal(1, CountingConnectionCatalogNeuron.ReadCalls);
            Assert.Equal(0, CountingComputeNeuron.ReadCalls);

            // 6. Navigating to "billing" queries Compute.
            await settings.Request("navigate", new SettingsNavigate("billing"));
            Assert.Equal(1, CountingComputeNeuron.ReadCalls);
            Assert.Equal(0, vault.StatusCalls);

            // 7. Navigating to "keys" queries SecretVault.
            await settings.Request("navigate", new SettingsNavigate("keys"));
            Assert.True(vault.StatusCalls > 0);
            Assert.Equal(0, CountingDeviceLinkNeuron.StatusCalls);

            // 8. Navigating to "devices" (general/devices subpage) queries DeviceLink.
            await settings.Request("navigate", new SettingsNavigate("devices"));
            Assert.Equal("general", settings.App.State.Section);
            Assert.Equal("devices", settings.App.State.SubPage);
            Assert.Equal(1, CountingDeviceLinkNeuron.StatusCalls);
        }
        finally
        {
            CountingApprovalsNeuron.Reset();
            CountingSynapseStudioNeuron.Reset();
            CountingModelCatalogNeuron.Reset();
            CountingConnectionCatalogNeuron.Reset();
            CountingComputeNeuron.Reset();
            CountingDeviceLinkNeuron.Reset();
        }
    }

    [Fact]
    public void SettingsProviderProjection_MatchesBetweenCatalogAndFallbackWithSharedRuntimeFormatting()
    {
        var deviceView = new DeviceView(
            "machine-1",
            new Hardware(32L * 1024 * 1024 * 1024, 16L * 1024 * 1024 * 1024, [Accelerator.Cpu, Accelerator.Cuda]),
            [
                new RuntimeStatus(RuntimeId.Ollama, true, "0.9.1", new Uri("http://127.0.0.1:11434")),
                new RuntimeStatus(RuntimeId.FoundryLocal, false, null, null),
                new RuntimeStatus(RuntimeId.OpenAiCompat("lmstudio"), true, "0.3.5", new Uri("http://127.0.0.1:1234/v1")),
            ],
            [new DeviceModel(KnownModels.Qwen35, RuntimeId.Ollama, InstallState.Ready, Fit.Good)],
            []);

        var state = new SettingsAppState
        {
            Device = deviceView,
            LocalOllamaEndpoint = "http://127.0.0.1:11434",
            Variables = [new UserVariableEntry("OPENAI_API_KEY", "vault://OPENAI_API_KEY")],
        };

        var fallbackRows = SettingsProviderProjection.DefaultProviders(state);
        var openAiCard = KnownModels.All.First(static c => c.Provider == AiProvider.OpenAI);
        var catalogRows = SettingsProviderProjection.BuildProviderRows(
            [openAiCard],
            new Dictionary<string, ModelAvailability>(StringComparer.OrdinalIgnoreCase)
            {
                [openAiCard.Id.Value] = new ModelAvailability(openAiCard.Id, true, null),
            },
            deviceView,
            state.LocalOllamaEndpoint);

        Assert.Equal(fallbackRows.Length, catalogRows.Length);
        for (var i = 0; i < fallbackRows.Length; i++)
        {
            Assert.Equal(fallbackRows[i].Provider, catalogRows[i].Provider);
            Assert.Equal(fallbackRows[i].Label, catalogRows[i].Label);
            Assert.Equal(fallbackRows[i].Icon, catalogRows[i].Icon);
            Assert.Equal(fallbackRows[i].Status, catalogRows[i].Status);
        }

        Assert.Contains(fallbackRows, static r => r.Label == "Ollama" && r.Status.Contains("v0.9.1", StringComparison.Ordinal) && r.Status.Contains("1 installed", StringComparison.Ordinal));
        Assert.Contains(fallbackRows, static r => r.Label == "lmstudio" && r.Status.Contains("v0.3.5", StringComparison.Ordinal));
        Assert.Contains(fallbackRows, static r => r.Provider == AiProvider.OpenAI && r.Status == "Ready");
        Assert.Contains(fallbackRows, static r => r.Provider == AiProvider.Anthropic && r.Status == "Add a key in API keys");
        Assert.Equal("32 GB RAM · 16 GB VRAM · CUDA", SettingsProviderProjection.FormatHardwareLine(deviceView.Hardware));

        var defaultRoles = SettingsProviderProjection.DefaultRoles(state);
        Assert.Equal(Enum.GetValues<ModelRole>().Length, defaultRoles.Length);
        Assert.All(defaultRoles, static row => Assert.NotEmpty(row.Choices));
    }

    [Fact]
    public void ExtractedSectionRenderers_PreserveWireKeysSubPagesAndModalSheetsAcrossAllBoundedContexts()
    {
        (string Section, string? SubPage, string? Sheet, string ExpectedWireMarker)[] cases =
        [
            ("general", null, null, "Appearance \\u0026 Regional Preferences"),
            ("general", "devices", null, "Connect this PC to IntoChat Cloud"),
            ("desktop", null, null, "Desktop canvas tone"),
            ("models", null, null, "Connected Client Compute"),
            ("models", "local-server", null, "Ollama Local Server"),
            ("models", "web-tools", null, "MCP tool servers"),
            ("connections", null, null, "Active Connections"),
            ("connections", "github", null, "ConnectionPage"),
            ("files", null, null, "Mounted file roots"),
            ("keys", null, null, "keys:open-vault"),
            ("variables", null, null, "Variables and secrets"),
            ("apps", null, null, "apps:inspect-ino"),
            ("apps", "memory", null, "Ino Memory Store"),
            ("billing", null, null, "billing-cap-50"),
            ("team", null, null, "team:invite"),
            ("team", "owner", null, "PersonPage"),
            ("notifications", null, null, "No pending approvals"),
            ("keyboard", null, null, "Keyboard shortcuts"),
            ("danger", null, "confirm-delete", "Sheet \\u00B7 confirm-delete"),
        ];

        foreach (var (section, subPage, sheet, marker) in cases)
        {
            var state = new SettingsAppState().Navigate(section, subPage, sheet);
            var node = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(state);
            var wire = JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));
            Assert.Contains(marker, wire, StringComparison.Ordinal);
            Assert.Contains("\"key\":\"settings-shell\"", wire, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsHostServices_ResolvesOptionalCollaboratorsFromDiAndSupportsParameterlessCtor()
    {
        var vault = new MemorySecretVault(["OPENAI_API_KEY"]);
        var tempPath = Path.Combine(Path.GetTempPath(), $"intochat-ui10-machine-{Guid.NewGuid():N}");
        var machine = new MachineId(Path.Combine(tempPath, "machine-id"));
        var sp = new ServiceCollection()
            .AddSingleton<ISecretVault>(vault)
            .AddSingleton(machine)
            .BuildServiceProvider();

        try
        {
            var fromDi = ActivatorUtilities.CreateInstance<SettingsApp>(sp);
            Assert.Same(vault, fromDi.SecretVault);
            Assert.Same(machine, fromDi.Services.ThisMachine);

            var standalone = Activator.CreateInstance<SettingsApp>();
            Assert.Null(standalone.SecretVault);
            Assert.Null(standalone.Services.ThisMachine);
        }
        finally
        {
            if (Directory.Exists(tempPath)) { Directory.Delete(tempPath, recursive: true); }
        }
    }

    private sealed class CountingSecretVault : ISecretVault
    {
        public int StatusCalls { get; private set; }

        public Task<bool> HasSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult(false);
        }

        public Task<string> GetSecretStatusAsync(string secretName, CancellationToken cancellationToken = default)
        {
            StatusCalls++;
            return Task.FromResult("Not set");
        }
    }

    [Neuron("approvals"), StatelessNeuron]
    private sealed class CountingApprovalsNeuron : Neuron, IApprovals
    {
        public static int PendingCalls { get; private set; }
        public static void Reset() => PendingCalls = 0;

        public Task<IReadOnlyList<Approval>> Pending(CancellationToken ct = default)
        {
            PendingCalls++;
            return Task.FromResult<IReadOnlyList<Approval>>([]);
        }
    }

    [Neuron("ino.synapses"), StatelessNeuron]
    private sealed class CountingSynapseStudioNeuron : Neuron, ISynapseStudio
    {
        public static int ListCalls { get; private set; }
        public static void Reset() => ListCalls = 0;

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult(""));
        public Task<InoToolResult> Deploy(string alias, string source) => Task.FromResult(new InoToolResult(""));
        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
            => Task.FromResult(new UnitDeployResult(true, "", DeployStatus.Deployed, "h", new Dictionary<string, InstallOutcome>()));
        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult(""));
        public Task<DeployedSource[]> List()
        {
            ListCalls++;
            return Task.FromResult<DeployedSource[]>([]);
        }
        public Task<Dictionary<string, string>> Sidecars(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<Dictionary<string, string>> SourceFiles(string alias) => Task.FromResult(new Dictionary<string, string>());
        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("", Ok: true));
    }

    [Neuron("ai.models"), StatelessNeuron]
    private sealed class CountingModelCatalogNeuron : Neuron, IModelCatalog
    {
        public static int ListCalls { get; private set; }
        public static void Reset() => ListCalls = 0;

        public Task<ModelCard[]> List(Abilities need = Abilities.None)
        {
            ListCalls++;
            return Task.FromResult(KnownModels.All.ToArray());
        }

        public Task<ModelId> Pick(ModelRole role, ModelId? turn = null)
            => Task.FromResult(turn ?? KnownModels.Defaults.Device[ModelRole.Chat]);
        public Task SetRole(ModelRole role, ModelId model) => Task.CompletedTask;
        public Task<RoleMap> Roles() => Task.FromResult(KnownModels.Defaults);
        public Task<ModelAvailability[]> Availability() => Task.FromResult<ModelAvailability[]>([]);
        public Task SaveProviderCredential(string provider, string credentialValue) => Task.CompletedTask;
    }

    [Neuron(IConnectionCatalog.TypeName), StatelessNeuron]
    private sealed class CountingConnectionCatalogNeuron : Neuron, IConnectionCatalog
    {
        public static int ReadCalls { get; private set; }
        public static void Reset() => ReadCalls = 0;

        public Task Subscribed() => Task.CompletedTask;
        public Task<ConnectionCatalog> Read()
        {
            ReadCalls++;
            return Task.FromResult(new ConnectionCatalog { Rows = [], Revision = 1 });
        }
    }

    [Neuron("ui.compute"), StatelessNeuron]
    private sealed class CountingComputeNeuron : Neuron, ICompute
    {
        public static int ReadCalls { get; private set; }
        public static void Reset() => ReadCalls = 0;

        public Task<ComputeState> Read()
        {
            ReadCalls++;
            return Task.FromResult(new ComputeState(0, DateTimeOffset.UtcNow, [], ComputeState.DefaultMonthlyBudgetMicros));
        }

        public Task<ComputeState> Record(string key, long micros)
            => Task.FromResult(new ComputeState(micros, DateTimeOffset.UtcNow, [], ComputeState.DefaultMonthlyBudgetMicros));

        public Task<ComputeState> SetBudget(long monthlyBudgetMicros)
            => Task.FromResult(new ComputeState(0, DateTimeOffset.UtcNow, [], monthlyBudgetMicros));
    }

    [Neuron("devices.link"), StatelessNeuron]
    private sealed class CountingDeviceLinkNeuron : Neuron, IDeviceLink
    {
        public static int StatusCalls { get; private set; }
        public static void Reset() => StatusCalls = 0;

        public Task<LinkStatus> Join(Uri server, Code code)
            => Task.FromResult(new LinkStatus(LinkState.Connected, server, "pc", null, AllowClientCodeSandbox: true));

        public Task<LinkStatus> Status()
        {
            StatusCalls++;
            return Task.FromResult(new LinkStatus(LinkState.NotJoined, null, "", null, AllowClientCodeSandbox: true));
        }

        public Task<LinkStatus> SetAllowClientCodeSandbox(bool allow)
            => Task.FromResult(new LinkStatus(LinkState.NotJoined, null, "", null, AllowClientCodeSandbox: allow));

        public Task Leave() => Task.CompletedTask;
    }
}
