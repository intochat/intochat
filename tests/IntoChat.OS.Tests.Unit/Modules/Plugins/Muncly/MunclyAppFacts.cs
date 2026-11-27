using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.Muncly;
using IntoChat.Muncly.Screens;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Tools;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Components.Muncly;

[CollectionDefinition(nameof(MunclyAppFacts), DisableParallelization = true)]
public sealed class MunclyAppFactsCollection;

[Collection(nameof(MunclyAppFacts))]
public sealed class MunclyAppFacts
{
    [Fact]
    public void StateEnforcesHttpsUrlAndSafeDeepLinks()
    {
        var initial = new MunclyAppState();
        Assert.True(initial.ValidBaseUrlInvariant);
        Assert.True(initial.ValidPathInvariant);
        Assert.Equal("https://mcpdemo-crm.munc.ly", initial.CurrentUrl);

        var connected = initial.ConnectEndpoint("https://acme.munc.ly/dashboard");
        Assert.True(connected.IsAccepted);
        Assert.Equal("https://acme.munc.ly", connected.Next!.BaseUrl);

        var opened = connected.Next.OpenPath(null, objectName: "account", recordId: "acc-42");
        Assert.True(opened.IsAccepted);
        Assert.Equal("https://acme.munc.ly/objects/account/acc-42", opened.Next!.CurrentUrl);

        var bad = initial.ConnectEndpoint("http://evil.example.com");
        Assert.False(bad.IsAccepted);

        var creds = initial.ConnectEndpoint("https://user:pass@acme.munc.ly");
        Assert.False(creds.IsAccepted);

        var loopback = initial.ConnectEndpoint("http://localhost:8080");
        Assert.True(loopback.IsAccepted);
    }

    [Fact]
    public void OptionsBuildMcpEndpointWithOptionalToolSetsAndValidate()
    {
        var opts = new MunclyOptions
        {
            BaseUrl = "https://acme.munc.ly/",
            ToolSets = "core,setup",
            AllowedTools = ["list_records", "describe_object"],
        };
        opts.Validate();
        Assert.Equal("https://acme.munc.ly/api/mcp?sets=core%2Csetup", opts.BuildMcpEndpointUrl());

        var badUrl = new MunclyOptions { BaseUrl = "http://not-https.example.com" };
        Assert.Throws<ArgumentException>(badUrl.Validate);

        var badTools = new MunclyOptions { BaseUrl = "https://acme.munc.ly", AllowedTools = [""] };
        Assert.Throws<ArgumentException>(badTools.Validate);
    }

    [Fact]
    public void MunclyModuleIntegrationExposesNoPersonalCredentials()
    {
        var integration = MunclyModule.Integration;
        Assert.DoesNotContain(integration.AllFields, f =>
            string.Equals(f, "WebEmail", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, "WebPassword", StringComparison.OrdinalIgnoreCase)
            || string.Equals(f, "McpToken", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(integration.SecretFields);
        Assert.Empty(integration.OptionalSecretFields);
    }

    [Fact]
    public async Task MunclyAppHydratesConfiguredBaseUrlAndRendersEmbeddedWebBrowser()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MunclyAppState.ConfiguredUrlKey] = "https://acme.munc.ly",
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services)
            .RegisterCatalog()
            .Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        os.Register<MunclyApp>();

        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.muncly", "default"));
        var opened = await screen.Open();

        Assert.NotNull(opened);
        var wire = opened!.View.GetRawText();
        Assert.Contains("https://acme.munc.ly", wire, StringComparison.Ordinal);
        Assert.Contains("muncly:open-browser", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredMunclyAppShowsServerSideFormControlCard()
    {
        await using var os = TestOS.Local()
            .RegisterCatalog()
            .Register(typeof(IntoChat.OS.Modules.UI.UiModule).Assembly);
        os.Register<MunclyApp>();

        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.muncly", "default"));
        var opened = await screen.Open();

        Assert.NotNull(opened);
        var wire = opened!.View.GetRawText();
        Assert.Contains("Connect Muncly CRM", wire, StringComparison.Ordinal);
        Assert.Contains("/control.form/", wire, StringComparison.Ordinal);
        Assert.Contains(MunclyApp.ConnectFormKey, wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MunclyAppConnectRoundtripSharesConnectionWithSettingsAndNeverLeaksBearerToken()
    {
        FakeMunclyMcpConnectionNeuron.Reset();
        var rawToken = "crm_test_" + new string('t', 24);
        var tokenRef = SecretReferences.For("local", "app.muncly/default/muncly-connect/token", "MCP Bearer Token", isSet: true).Reference;
        var vaultPort = new InMemorySecretVaultPort(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [tokenRef] = rawToken,
        });

        var services = new ServiceCollection()
            .AddSingleton<ISecretVaultPort>(vaultPort)
            .BuildServiceProvider();

        try
        {
            var brain = new BrainId("local");
            await using var os = TestOS.Local(o => o.Services = services)
                .RegisterCatalog()
                .Register<FakeMunclyMcpConnectionNeuron>();
            os.Register<MunclyApp>();

            using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));
            var muncly = await os.Install<MunclyApp>();
            var opened = await muncly.Screen.Open();
            Assert.Contains("Connect Muncly CRM", opened!.View.GetRawText(), StringComparison.Ordinal);

            // Submit the server-side FormControl with URL + SecretRef
            var submit = new Submit(
                new Dictionary<string, string> { ["url"] = "https://mcpdemo-crm.munc.ly" },
                new Dictionary<string, string> { ["token"] = tokenRef });
            await muncly.Screen.Dispatch(new Command(
                MunclyApp.ConnectFormKey,
                "control.submit",
                JsonSerializer.SerializeToElement(submit, JsonSerializerOptions.Web)));
            await os.Idle();

            // Verify IConnection.Connect("bearer-token", ...) + Probe() were called on connection/mcp/muncly-crm
            Assert.Equal("connection/mcp/muncly-crm", FakeMunclyMcpConnectionNeuron.LastConnectedKey);
            Assert.Equal("bearer-token", FakeMunclyMcpConnectionNeuron.LastAuthMethodId);
            Assert.NotNull(FakeMunclyMcpConnectionNeuron.LastConnectPayload);
            Assert.Contains(rawToken, FakeMunclyMcpConnectionNeuron.LastConnectPayload!, StringComparison.Ordinal);
            Assert.Contains("https://mcpdemo-crm.munc.ly/api/mcp", FakeMunclyMcpConnectionNeuron.LastConnectPayload!, StringComparison.Ordinal);
            Assert.True(FakeMunclyMcpConnectionNeuron.ProbeCount >= 1);

            Assert.True(muncly.App.State.IsConfiguredEndpoint);
            Assert.Equal(ConnectionStatus.Connected, muncly.App.State.ConnectionStatus);
            Assert.Equal(["list_contacts", "create_deal"], muncly.App.State.DiscoveredTools);

            // Toggle live MCP tools inspector and verify toolbar + external launch + tools list
            await muncly.Screen.Click("muncly:toggle-inspector");
            await os.Idle();
            Assert.True(muncly.App.State.ShowToolsInspector);

            var connectedWire = JsonSerializer.Serialize(muncly.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("Open Muncly CRM in Browser", connectedWire, StringComparison.Ordinal);
            Assert.Contains("Connected CRM \\u0026 Live MCP Tools Inspector", connectedWire, StringComparison.Ordinal);
            Assert.Contains("list_contacts", connectedWire, StringComparison.Ordinal);
            Assert.Contains("create_deal", connectedWire, StringComparison.Ordinal);

            // Prove Settings -> Connections sees the SAME connection/mcp/muncly-crm as Connected (2 tools)!
            var settings = await os.Install<SettingsApp>();
            settings.App.Observer = os;
            await settings.App.Navigate(new SettingsNavigate("connections"));
            Assert.Contains(settings.App.State.ActiveConnections, c =>
                c.Key == "connection/mcp/muncly-crm"
                && c.Status == ConnectionStatus.Connected
                && c.ToolCount == 2);

            // Prove ZERO raw secrets in MunclyAppState, View, or journal
            var serializedState = JsonSerializer.Serialize(muncly.App.State);
            Assert.DoesNotContain(rawToken, serializedState, StringComparison.Ordinal);
            Assert.DoesNotContain(rawToken, connectedWire, StringComparison.Ordinal);

            // Re-probe and Disconnect
            await muncly.Screen.Click("muncly:probe");
            await os.Idle();
            Assert.True(FakeMunclyMcpConnectionNeuron.ProbeCount >= 2);

            await muncly.Screen.Click("muncly:disconnect");
            await os.Idle();
            Assert.False(muncly.App.State.IsConfiguredEndpoint);
            Assert.Equal(ConnectionStatus.Disconnected, muncly.App.State.ConnectionStatus);
            Assert.Empty(muncly.App.State.DiscoveredTools);
        }
        finally
        {
            FakeMunclyMcpConnectionNeuron.Reset();
        }
    }

    [Fact]
    public async Task MunclyAppWithConnectionSecretBinder_UsesCanonicalConnectionKeyWithoutEagerUnseal()
    {
        var binder = new RecordingMunclySecretBinder();
        var throwingPort = new ThrowingSecretVaultPort();
        var tokenRef = SecretReferences.For("local", "app.muncly/default/muncly-connect/token", "MCP Bearer Token", isSet: true).Reference;

        var services = new ServiceCollection()
            .AddSingleton<ISecretVaultPort>(throwingPort)
            .AddSingleton<IConnectionSecretBinder>(binder)
            .BuildServiceProvider();

        var brain = new BrainId("local");
        await using var os = TestOS.Local(o => o.Services = services)
            .RegisterCatalog()
            .Register<FakeMunclyMcpConnectionNeuron>();
        os.Register<MunclyApp>();

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));
        var muncly = await os.Install<MunclyApp>();
        await muncly.Screen.Open();

        var submit = new Submit(
            new Dictionary<string, string> { ["url"] = "https://mcpdemo-crm.munc.ly" },
            new Dictionary<string, string> { ["token"] = tokenRef });
        await muncly.Screen.Dispatch(new Command(
            MunclyApp.ConnectFormKey,
            "control.submit",
            JsonSerializer.SerializeToElement(submit, JsonSerializerOptions.Web)));
        await os.Idle();

        Assert.Equal(0, throwingPort.ResolveCalls);
        Assert.NotNull(binder.LastConnectionKey);
        var parsed = ConnectionKeys.Parse(binder.LastConnectionKey!);
        Assert.Equal("local", parsed.BrainId);
        Assert.Equal("mcp", parsed.ConnectorId);
        Assert.Equal(MunclyOptions.DefaultMcpServerName, parsed.ConnectionId);
        Assert.Equal(tokenRef, binder.LastSecretRef);
        Assert.Equal("bearer-token", binder.LastAuthMethodId);
    }

    private sealed class ThrowingSecretVaultPort : ISecretVaultPort
    {
        public int ResolveCalls { get; private set; }
        public Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken) => Task.FromResult(SecretVaultResult.Denied);
        public Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken) => Task.FromResult(SecretVaultStatus.Ok);
        public Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            throw new InvalidOperationException("ISecretVaultPort must not be eagerly called when IConnectionSecretBinder is present.");
        }
    }

    private sealed class RecordingMunclySecretBinder : IConnectionSecretBinder
    {
        public string? LastConnectionKey { get; private set; }
        public string? LastSecretRef { get; private set; }
        public string? LastAuthMethodId { get; private set; }

        public Task<ConnectionView> BindAsync(
            BrainId brain,
            string connectorId,
            string connectionKey,
            string authMethodId,
            string secretRef,
            Func<string, string>? formatValue = null,
            bool probe = true,
            CancellationToken ct = default)
        {
            LastConnectionKey = connectionKey;
            LastSecretRef = secretRef;
            LastAuthMethodId = authMethodId;
            return Task.FromResult(new ConnectionView(connectorId, ConnectionKeys.Parse(connectionKey).ConnectionId, authMethodId, ConnectionStatus.Connected, null, null, DateTimeOffset.UtcNow, null));
        }

        public Task<ConnectionView> BindGrantAsync(
            BrainId brain,
            string connectorId,
            string connectionKey,
            string authMethodId,
            string secretRef,
            string? target,
            string? scopes = null,
            Func<string, string>? formatValue = null,
            bool probe = true,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class InMemorySecretVaultPort(Dictionary<string, string> secrets) : ISecretVaultPort
    {
        public Task<SecretVaultResult> Set(string name, string label, string value, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultResult.Denied);

        public Task<SecretVaultStatus> Remove(string name, CancellationToken cancellationToken)
            => Task.FromResult(SecretVaultStatus.Ok);

        public Task<SecretVaultResult> Resolve(SecretRef secret, CancellationToken cancellationToken)
            => Task.FromResult(secrets.TryGetValue(secret.Reference, out var val)
                ? SecretVaultResult.Resolved(val)
                : SecretVaultResult.NotFound);
    }
}

[Neuron("mcp")]
public sealed class FakeMunclyMcpConnectionNeuron : Neuron, IConnection, IMcpToolConnection
{
    public static string? LastConnectedKey { get; private set; }
    public static string? LastAuthMethodId { get; private set; }
    public static string? LastConnectPayload { get; private set; }
    public static int ProbeCount { get; private set; }
    public static bool IsConnected { get; private set; }
    public static string CurrentUrl { get; private set; } = string.Empty;

    public static void Reset()
    {
        LastConnectedKey = null;
        LastAuthMethodId = null;
        LastConnectPayload = null;
        ProbeCount = 0;
        IsConnected = false;
        CurrentUrl = string.Empty;
    }

    Task<ConnectionView> IConnection.Read()
        => Task.FromResult(new ConnectionView(
            "mcp",
            Id.Key.Split('/').Last(),
            LastAuthMethodId ?? "bearer-token",
            IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
            CurrentUrl,
            null,
            DateTimeOffset.UtcNow,
            null));

    public Task<ConnectionView> Connect(string authMethodId, string value)
    {
        LastConnectedKey = Id.Key;
        LastAuthMethodId = authMethodId;
        LastConnectPayload = value;
        IsConnected = true;
        using var doc = JsonDocument.Parse(value);
        if (doc.RootElement.TryGetProperty("url", out var urlProp))
        {
            CurrentUrl = urlProp.GetString() ?? string.Empty;
        }
        return ((IConnection)this).Read();
    }

    public Task<ConnectionView> Probe()
    {
        ProbeCount++;
        return ((IConnection)this).Read();
    }

    public Task Disconnect()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<ConnectionView> Adopt(string authMethodId, string? account) => ((IConnection)this).Read();
    public Task<Dictionary<string, SealedSecret>> ExportFor(string brainId) => Task.FromResult(new Dictionary<string, SealedSecret>());
    public Task<ConnectionView> Import(string authMethodId, string? account, Dictionary<string, SealedSecret> secrets, ConnectionStatus status, string? detail) => ((IConnection)this).Read();
    public Task ShareInto(string brainId, string app) => Task.CompletedTask;
    public Task<bool> IsSharedInto(string brainId, string app) => Task.FromResult(false);
    public Task UnshareFrom(string brainId, string app) => Task.CompletedTask;
    public Task ShareWith(string principalId) => Task.CompletedTask;
    public Task<bool> IsSharedWith(string principalId) => Task.FromResult(false);
    public Task UnshareWith(string principalId) => Task.CompletedTask;

    public Task<McpServerState> ConnectServer(McpConnectionConfig config, CancellationToken ct = default)
    {
        LastConnectedKey = Id.Key;
        CurrentUrl = config.Url;
        IsConnected = true;
        return ReadState(ct);
    }

    public Task<McpServerState> Configure(McpConnectionConfig config, CancellationToken ct = default) => ConnectServer(config, ct);

    public Task DisconnectServer(CancellationToken ct = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<McpServerState> ReadState(CancellationToken ct = default)
        => Task.FromResult(new McpServerState(
            "Muncly CRM",
            CurrentUrl,
            IsConnected,
            IsConnected
                ? [
                    new ToolDescriptor("list_contacts", "List Contacts", "List CRM contacts", "{}", true),
                    new ToolDescriptor("create_deal", "Create Deal", "Create a CRM deal", "{}", false),
                  ]
                : [],
            null,
            DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<string>> List() => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<ToolSet> Tools(CancellationToken ct = default) => Task.FromResult(new ToolSet("Muncly CRM", []));
    public Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
    public Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default) => Task.FromResult(true);
    public Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default) => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default) => Task.FromResult(new ToolResult(false, "{}", []));
}
