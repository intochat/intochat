using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Catalog;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Tools;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

[Neuron(IConnectionCatalog.TypeName)]
public sealed class FakeSettingsConnectionCatalog : Neuron, IConnectionCatalog
{
    public static CatalogRow[] Rows { get; set; } = [];

    public static void Reset() => Rows = [];

    public Task Subscribed() => Task.CompletedTask;

    public Task<ConnectionCatalog> Read()
        => Task.FromResult(new ConnectionCatalog { Rows = Rows, Revision = 1 });
}

[Neuron("github")]
public sealed class FakeGitHubConnectionNeuron : Neuron, IConnection, IConnectionInbound
{
    public static OAuthGrant? LastGrant { get; set; }
    public static ConnectionStatus CurrentStatus { get; set; } = ConnectionStatus.Disconnected;
    public static string? CurrentAccount { get; set; }
    public static int ProbeCount { get; set; }
    public static int DisconnectCount { get; set; }
    public static Exception? ThrowOnConnect { get; set; }

    public static void Reset()
    {
        LastGrant = null;
        CurrentStatus = ConnectionStatus.Disconnected;
        CurrentAccount = null;
        ProbeCount = 0;
        DisconnectCount = 0;
        ThrowOnConnect = null;
    }

    Task<ConnectionView> IConnection.Read()
        => Task.FromResult(new ConnectionView(
            "github",
            Id.Key.Split('/').Last(),
            "pat",
            CurrentStatus,
            CurrentAccount,
            null,
            DateTimeOffset.UtcNow,
            null));

    public Task<ConnectionView> Connect(string authMethodId, string value)
    {
        if (ThrowOnConnect is not null)
        {
            throw ThrowOnConnect;
        }

        CurrentStatus = ConnectionStatus.Connected;
        return ((IConnection)this).Read();
    }

    public Task<ConnectionView> Probe()
    {
        ProbeCount++;
        return ((IConnection)this).Read();
    }

    public Task Disconnect()
    {
        DisconnectCount++;
        CurrentStatus = ConnectionStatus.Disconnected;
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

    public Task<ConnectionView> AcceptGrant(OAuthGrant grant)
    {
        if (ThrowOnConnect is not null)
        {
            throw ThrowOnConnect;
        }

        LastGrant = grant;
        CurrentStatus = ConnectionStatus.Connected;
        CurrentAccount = grant.Target;
        return ((IConnection)this).Read();
    }
}

[Neuron("mcp")]
public sealed class FakeBearerMcpConnectionNeuron : Neuron, IConnection, IMcpToolConnection
{
    public static string? LastConnectedKey { get; private set; }
    public static string? LastAuthMethodId { get; private set; }
    public static string? LastConnectPayload { get; private set; }
    public static int ProbeCount { get; private set; }
    public static bool Connected { get; private set; }
    public static string CurrentUrl { get; private set; } = string.Empty;
    public static string CurrentTitle { get; private set; } = string.Empty;

    public static void Reset()
    {
        LastConnectedKey = null;
        LastAuthMethodId = null;
        LastConnectPayload = null;
        ProbeCount = 0;
        Connected = false;
        CurrentUrl = string.Empty;
        CurrentTitle = string.Empty;
    }

    Task<ConnectionView> IConnection.Read()
        => Task.FromResult(new ConnectionView(
            "mcp",
            Id.Key.Split('/').Last(),
            LastAuthMethodId ?? "bearer-token",
            Connected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected,
            CurrentUrl,
            null,
            DateTimeOffset.UtcNow,
            null));

    public Task<ConnectionView> Connect(string authMethodId, string value)
    {
        LastConnectedKey = Id.Key;
        LastAuthMethodId = authMethodId;
        LastConnectPayload = value;
        Connected = true;
        using var doc = JsonDocument.Parse(value);
        if (doc.RootElement.TryGetProperty("url", out var urlProp)) { CurrentUrl = urlProp.GetString() ?? ""; }
        if (doc.RootElement.TryGetProperty("title", out var titleProp)) { CurrentTitle = titleProp.GetString() ?? ""; }
        return ((IConnection)this).Read();
    }

    public Task<ConnectionView> Probe()
    {
        ProbeCount++;
        return ((IConnection)this).Read();
    }

    public Task Disconnect()
    {
        Connected = false;
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
        CurrentTitle = config.Title ?? "";
        Connected = true;
        return ReadState(ct);
    }

    public Task<McpServerState> Configure(McpConnectionConfig config, CancellationToken ct = default) => ConnectServer(config, ct);

    public Task DisconnectServer(CancellationToken ct = default)
    {
        Connected = false;
        return Task.CompletedTask;
    }

    public Task<McpServerState> ReadState(CancellationToken ct = default)
        => Task.FromResult(new McpServerState(
            CurrentTitle,
            CurrentUrl,
            Connected,
            Connected ? [new ToolDescriptor("crm_list", "List", "List records", "{}", true)] : [],
            null,
            DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<string>> List() => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<ToolSet> Tools(CancellationToken ct = default) => Task.FromResult(new ToolSet(CurrentTitle, []));
    public Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolDescriptor>>([]);
    public Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default) => Task.FromResult(true);
    public Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default) => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    public Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default) => Task.FromResult(new ToolResult(false, "{}", []));
}

[CollectionDefinition(nameof(SettingsGitHubHubFacts), DisableParallelization = true)]
public sealed class SettingsGitHubHubCollection;

[Collection(nameof(SettingsGitHubHubFacts))]
public sealed class SettingsGitHubHubFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConnectionsHub_RendersLiveCatalogRows_RealPort5080Webhook_AndProbeDisconnectActions()
    {
        FakeSettingsConnectionCatalog.Reset();
        FakeGitHubConnectionNeuron.Reset();
        FakeGitHubConnectionNeuron.CurrentStatus = ConnectionStatus.Connected;
        FakeGitHubConnectionNeuron.CurrentAccount = "intochat/os";
        FakeSettingsConnectionCatalog.Rows =
        [
            new CatalogRow(ConnectionKeys.For("local", "github", "main"), "github", "main", ConnectionStatus.Connected, "intochat/os", DateTimeOffset.UtcNow),
            new CatalogRow(ConnectionKeys.For("local", "google-drive", "primary"), "google-drive", "primary", ConnectionStatus.Expired, "ada@example.com", DateTimeOffset.UtcNow),
            new CatalogRow(ConnectionKeys.For("local", "mcp", "muncly-crm"), "mcp", "muncly-crm", ConnectionStatus.Connected, "https://mcpdemo-crm.munc.ly/api/mcp", DateTimeOffset.UtcNow),
        ];

        try
        {
            var brain = new BrainId("local");
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeSettingsConnectionCatalog>()
                .Register<FakeGitHubConnectionNeuron>();

            var settings = await os.Install<SettingsApp>();
            settings.App.Connectors =
            [
                new ConnectorDefinition("github", "GitHub", ["device-flow", "oauth", "pat"], []),
                new ConnectorDefinition("muncly", "Muncly CRM", ["bearer-token"], []),
                new ConnectorDefinition("google-drive", "Google Drive", ["oauth"], []),
                new ConnectorDefinition("gmail", "Gmail", ["oauth"], []),
                new ConnectorDefinition("salesforce", "Salesforce", ["oauth"], []),
            ];

            await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), transition: ViewTransition.SharedAxisX, cancellationToken: Ct);
            await os.Idle();

            Assert.Equal("connections", settings.App.State.Section);
            Assert.Equal(ViewTransition.SharedAxisX, settings.App.ActiveTransition);
            Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/github/main" && c.Status == ConnectionStatus.Connected);
            Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/google-drive/primary" && c.Status == ConnectionStatus.Expired);
            Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/mcp/muncly-crm" && c.Status == ConnectionStatus.Connected);
            Assert.DoesNotContain(settings.App.State.ActiveConnections, c => c.Key.StartsWith("local/", StringComparison.Ordinal));

            // 4-part MCP CatalogRow deduplicates cleanly with MCP server rows and removes cleanly on McpRemove
            await settings.App.McpRemove(new SettingsMcpRemove("muncly-crm"));
            Assert.DoesNotContain(settings.App.State.ActiveConnections, c => c.Key == "connection/mcp/muncly-crm");

            var wire = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("http://localhost:5080/integrations/github/webhook", wire, StringComparison.Ordinal);
            Assert.DoesNotContain("localhost:5000", wire, StringComparison.Ordinal);
            Assert.Contains("conn:probe:github:main", wire, StringComparison.Ordinal);
            Assert.Contains("conn:disconnect:github:main", wire, StringComparison.Ordinal);
            Assert.Contains("conn:oauth:google-drive", wire, StringComparison.Ordinal);
            Assert.Contains("conn:oauth:gmail", wire, StringComparison.Ordinal);
            Assert.Contains("conn:oauth:salesforce", wire, StringComparison.Ordinal);

            // Probe and Disconnect actions
            await settings.Screen.Click("conn:probe:github:main");
            await os.Idle();
            Assert.Equal(1, FakeGitHubConnectionNeuron.ProbeCount);

            await settings.Screen.Click("conn:disconnect:github:main");
            await os.Idle();
            Assert.Equal(1, FakeGitHubConnectionNeuron.DisconnectCount);

            // INavigatedFrom departure guard still enforced
            var dirty = new SettingsAppState().MarkUnsavedChanges();
            Assert.True(dirty.CanDepart().Broken);
            Assert.False(dirty.CanDepart(force: true).Broken);
        }
        finally
        {
            FakeSettingsConnectionCatalog.Reset();
            FakeGitHubConnectionNeuron.Reset();
        }
    }

    [Fact]
    public async Task GitHubPatFormSubmission_SealsPatAndWebhookSecretWithoutLeakingRawSecrets()
    {
        FakeGitHubConnectionNeuron.Reset();
        var pat = "github_pat_" + new string('a', 32);
        var webhookSecret = "whsec_" + new string('w', 24);
        var patRef = SecretReferences.For("local", "app.settings/default/github-pat/PersonalAccessToken", "Personal access token", isSet: true).Reference;
        var webhookRef = SecretReferences.For("local", "app.settings/default/github-pat/WebhookSecret", "Webhook secret", isSet: true).Reference;

        try
        {
            var brain = new BrainId("local");
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeGitHubConnectionNeuron>();
            using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));

            var settings = await os.Install<SettingsApp>();
            var vault = new MemorySecretVault();
            vault.SetSecret(patRef, pat);
            vault.SetSecret(webhookRef, webhookSecret);
            settings.App.SecretVault = vault;

            await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), cancellationToken: Ct);
            await os.Idle();

            var secretRefs = new Dictionary<string, string> { ["PersonalAccessToken"] = patRef, ["WebhookSecret"] = webhookRef };

            // 1. Raw secret in values is refused at FormControl gate
            var raw = await Assert.ThrowsAsync<ActivationRefusedException>(() => SubmitForm(settings, SettingsControls.GitHubPat, new Dictionary<string, string>
            {
                ["Repository"] = "intochat/os",
                ["PersonalAccessToken"] = pat,
            }));
            Assert.Equal(FormRefusals.SecretInValues, raw.Reason);
            Assert.Null(FakeGitHubConnectionNeuron.LastGrant);

            // 2. Invalid repository format refused by domain rule
            await SubmitForm(settings, SettingsControls.GitHubPat, new Dictionary<string, string> { ["Repository"] = "invalid-repo" }, secretRefs);
            Assert.NotNull(settings.App.LastRejection);
            Assert.Contains("invalid_github_repository", settings.App.LastRejection!.Rules);

            // 3. Valid "owner/repo" seals BOTH PAT and WebhookSecret!
            await SubmitForm(settings, SettingsControls.GitHubPat, new Dictionary<string, string> { ["Repository"] = "intochat/os", ["ConnectionSlug"] = "main" }, secretRefs);

            Assert.Null(settings.App.LastRejection);
            Assert.NotNull(FakeGitHubConnectionNeuron.LastGrant);
            Assert.Equal("pat", FakeGitHubConnectionNeuron.LastGrant!.AuthMethodId);
            Assert.Equal("intochat/os", FakeGitHubConnectionNeuron.LastGrant.Target);
            Assert.Equal(pat, FakeGitHubConnectionNeuron.LastGrant.AccessToken);
            Assert.Contains(webhookSecret, FakeGitHubConnectionNeuron.LastGrant.Response, StringComparison.Ordinal);

            Assert.Contains(settings.App.State.ActiveConnections, c =>
                c.Key == "connection/github/main"
                && c.Status == ConnectionStatus.Connected
                && c.Account == "intochat/os");

            // 4. Prove ZERO raw secrets in state, view, or journal
            var serializedState = JsonSerializer.Serialize(settings.App.State);
            var serializedView = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            var settingsId = new NeuronId(brain, "app.settings", "default");
            var formId = new NeuronId(brain, "control.form", ScreenExtensions.ChildAddress(settingsId, SettingsControls.GitHubPat));
            var journalDump = string.Join("\n", os.Journal(brain).Signals(formId)
                .Concat(os.Journal(brain).Signals(settingsId))
                .Select(e => JsonSerializer.Serialize(e.Signal, e.Signal.GetType())));

            foreach (var secret in new[] { pat, webhookSecret })
            {
                Assert.DoesNotContain(secret, serializedState, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, serializedView, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, journalDump, StringComparison.Ordinal);
            }
        }
        finally
        {
            FakeGitHubConnectionNeuron.Reset();
        }
    }

    [Fact]
    public async Task MunclyAndAuthenticatedMcpForms_SealBearerTokenAndConnectViaIConnection()
    {
        FakeBearerMcpConnectionNeuron.Reset();
        var munclyToken = "crm_test_" + new string('m', 16);
        var customToken = "mcp_test_" + new string('c', 16);
        var munclyRef = SecretReferences.For("local", "app.settings/default/muncly-connect/BearerToken", "MCP Bearer Token", isSet: true).Reference;
        var customRef = SecretReferences.For("local", "app.settings/default/mcp-add/token", "Bearer token", isSet: true).Reference;

        try
        {
            var brain = new BrainId("local");
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeBearerMcpConnectionNeuron>();
            using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));

            var settings = await os.Install<SettingsApp>();
            var vault = new MemorySecretVault();
            vault.SetSecret(munclyRef, munclyToken);
            vault.SetSecret(customRef, customToken);
            settings.App.SecretVault = vault;

            await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), cancellationToken: Ct);
            await os.Idle();

            // 1. Connect Muncly CRM from Settings -> Connections
            await SubmitForm(
                settings,
                SettingsControls.MunclyConnect,
                new Dictionary<string, string> { ["Url"] = "https://mcpdemo-crm.munc.ly" },
                new Dictionary<string, string> { ["BearerToken"] = munclyRef });

            Assert.Equal("connection/mcp/muncly-crm", FakeBearerMcpConnectionNeuron.LastConnectedKey);
            Assert.Equal("bearer-token", FakeBearerMcpConnectionNeuron.LastAuthMethodId);
            Assert.Contains(munclyToken, FakeBearerMcpConnectionNeuron.LastConnectPayload!, StringComparison.Ordinal);
            Assert.Contains("https://mcpdemo-crm.munc.ly/api/mcp", FakeBearerMcpConnectionNeuron.LastConnectPayload!, StringComparison.Ordinal);
            Assert.Contains(settings.App.State.ActiveConnections, c => c.Key == "connection/mcp/muncly-crm" && c.Status == ConnectionStatus.Connected && c.ToolCount == 1);

            // 2. Connect authenticated custom MCP server via McpAddFormControl
            await SubmitForm(
                settings,
                SettingsControls.McpAddForm,
                new Dictionary<string, string> { ["name"] = "corp-mcp", ["url"] = "https://mcp.corp.example/api" },
                new Dictionary<string, string> { ["token"] = customRef });

            Assert.Equal("connection/mcp/corp-mcp", FakeBearerMcpConnectionNeuron.LastConnectedKey);
            Assert.Equal("bearer-token", FakeBearerMcpConnectionNeuron.LastAuthMethodId);
            Assert.Contains(customToken, FakeBearerMcpConnectionNeuron.LastConnectPayload!, StringComparison.Ordinal);
            Assert.Contains(settings.App.State.McpServers, m => m.Name == "corp-mcp" && m.Connected && m.ToolCount == 1);
            Assert.Contains(settings.App.State.ActiveConnections, c => c.Key == "connection/mcp/corp-mcp" && c.Status == ConnectionStatus.Connected);

            // 3. Prove ZERO raw tokens in state or rendered view
            var serializedState = JsonSerializer.Serialize(settings.App.State);
            var serializedView = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            foreach (var secret in new[] { munclyToken, customToken })
            {
                Assert.DoesNotContain(secret, serializedState, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, serializedView, StringComparison.Ordinal);
            }
        }
        finally
        {
            FakeBearerMcpConnectionNeuron.Reset();
        }
    }

    [Fact]
    public void InoAgentDefinition_ReferencesGitHubMcpTools_WithoutRetiredSynapseHint()
    {
        var def = InoAgentDefinition.For(new UserSettingsState("Ada", "dark", "qwen3.5:4b", []));
        Assert.Contains("GitHub MCP tools", def.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("studio_create_issue_triage_synapse", def.Instructions, StringComparison.Ordinal);
        if (typeof(IntoChat.OS.Modules.Microsoft.GitHub.IRepository).GetMethod("CreateIssue") is not null)
        {
            Assert.Contains("github_repository_create_issue", def.Instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConnectionsHubStates_PassAggregateExplorerPreviewWithZeroViolationsAndZeroMonospace()
    {
        var hubState = new SettingsAppState()
            .ApplyArrival(new SettingsNavArgs("connections"), ViewTransition.FadeThrough) with
        {
            ActiveConnections =
            [
                new SettingsConnectionRow("connection/github/main", "github", "main", "GitHub · main", ConnectionStatus.Connected, "intochat/os"),
                new SettingsConnectionRow("connection/mcp/muncly-crm", "mcp", "muncly-crm", "Muncly CRM", ConnectionStatus.Connected, "https://mcpdemo-crm.munc.ly/api/mcp", ToolCount: 5),
            ],
        };

        Assert.True(hubState.ValidConnectionsInvariant);
        Assert.True(hubState.ValidTransitionInvariant);
        var node = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(hubState);
        var wire = JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.DoesNotContain("\"mono\":true", wire, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Active Connections", wire, StringComparison.Ordinal);
        Assert.Contains("Connected \\u00B7 5 tools", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubDeviceFlow_StartAndComplete_RendersUserCodeExternalLinkAndConnects()
    {
        var flow = new FakeOAuthDeviceFlow();
        var brain = new BrainId("local");
        await using var os = TestOS.Local().RegisterCatalog();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));

        var settings = await os.Install<SettingsApp>();
        settings.App.OAuthDeviceFlow = flow;
        settings.App.Connectors =
        [
            new ConnectorDefinition("github", "GitHub", ["device", "oauth", "pat"], [])
            {
                DeviceMethods =
                [
                    new OAuthDeviceMethod(
                        "device",
                        "https://github.com/login/device/code",
                        "https://github.com/login/oauth/access_token",
                        ["repo"],
                        DefaultClientId: "test-client-id"),
                ],
            },
        ];
        await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), cancellationToken: Ct);
        await os.Idle();

        await settings.Screen.Click("settings:github:device-flow-start");
        await os.Idle();

        Assert.Equal("flow-gh-1", settings.App.State.DeviceFlowId);
        Assert.Equal("ABCD-1234", settings.App.State.DeviceFlowUserCode);
        Assert.Equal("device", flow.LastAuthMethodId);
        Assert.NotNull(flow.LastConnectionKey);
        var parsedDeviceKey = ConnectionKeys.Parse(flow.LastConnectionKey!);
        Assert.Equal("local", parsedDeviceKey.BrainId);
        Assert.Equal("github", parsedDeviceKey.ConnectorId);
        Assert.Equal("main", parsedDeviceKey.ConnectionId);

        var startedWire = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
        Assert.Contains("ABCD-1234", startedWire, StringComparison.Ordinal);
        Assert.Contains("https://github.com/login/device", startedWire, StringComparison.Ordinal);
        Assert.Contains("settings:github:device-flow-open", startedWire, StringComparison.Ordinal);
        Assert.Contains("settings:github:device-flow-complete", startedWire, StringComparison.Ordinal);

        await settings.Screen.Click("settings:github:device-flow-complete");
        await os.Idle();

        Assert.Null(settings.App.State.DeviceFlowId);
        Assert.Null(settings.App.State.DeviceFlowUserCode);
        Assert.Contains(settings.App.State.ActiveConnections, c =>
            c.Key == "connection/github/main"
            && c.Status == ConnectionStatus.Connected
            && c.Account == "octocat");
    }

    [Fact]
    public async Task GitHubPatFormSubmission_FailsClosedOnVaultUnsealFailureAndConnectFailure()
    {
        FakeGitHubConnectionNeuron.Reset();
        var pat = "github_pat_" + new string('b', 32);
        var patRef = SecretReferences.For("local", "app.settings/default/github-pat/PersonalAccessToken", "Personal access token", isSet: true).Reference;
        var webhookRef = SecretReferences.For("local", "app.settings/default/github-pat/WebhookSecret", "Webhook secret", isSet: true).Reference;

        try
        {
            var brain = new BrainId("local");
            await using var os = TestOS.Local()
                .RegisterCatalog()
                .Register<FakeGitHubConnectionNeuron>();
            using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));

            var settings = await os.Install<SettingsApp>();
            var vault = new MemorySecretVault();
            settings.App.SecretVault = vault;
            await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), cancellationToken: Ct);
            await os.Idle();

            // 1. Unopenable PAT ref fails closed: never passes secret:// URI as token, marks Failing
            await SubmitForm(
                settings,
                SettingsControls.GitHubPat,
                new Dictionary<string, string> { ["Repository"] = "intochat/os", ["ConnectionSlug"] = "main" },
                new Dictionary<string, string> { ["PersonalAccessToken"] = patRef });

            Assert.Null(FakeGitHubConnectionNeuron.LastGrant);
            var patFailRow = Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/github/main");
            Assert.Equal(ConnectionStatus.Failing, patFailRow.Status);
            Assert.Contains("unseal", patFailRow.Detail ?? "", StringComparison.OrdinalIgnoreCase);

            // 2. Valid PAT + unopenable WebhookSecret fails closed: never claims "Webhook secret sealed"
            vault.SetSecret(patRef, pat);
            await SubmitForm(
                settings,
                SettingsControls.GitHubPat,
                new Dictionary<string, string> { ["Repository"] = "intochat/os", ["ConnectionSlug"] = "main" },
                new Dictionary<string, string> { ["PersonalAccessToken"] = patRef, ["WebhookSecret"] = webhookRef });

            Assert.Null(FakeGitHubConnectionNeuron.LastGrant);
            var webhookFailRow = Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/github/main");
            Assert.Equal(ConnectionStatus.Failing, webhookFailRow.Status);
            Assert.DoesNotContain("Webhook secret sealed", webhookFailRow.Detail ?? "", StringComparison.OrdinalIgnoreCase);

            // 3. Valid PAT when AcceptGrant & Connect fail marks Failing (never Connected)
            FakeGitHubConnectionNeuron.ThrowOnConnect = new InvalidOperationException("Bad credentials");
            await SubmitForm(
                settings,
                SettingsControls.GitHubPat,
                new Dictionary<string, string> { ["Repository"] = "intochat/os", ["ConnectionSlug"] = "main" },
                new Dictionary<string, string> { ["PersonalAccessToken"] = patRef });

            var connectFailRow = Assert.Single(settings.App.State.ActiveConnections, c => c.Key == "connection/github/main");
            Assert.Equal(ConnectionStatus.Failing, connectFailRow.Status);
            Assert.Contains("Bad credentials", connectFailRow.Detail ?? "", StringComparison.Ordinal);
            Assert.Contains("Bad credentials", settings.App.State.Status, StringComparison.Ordinal);
        }
        finally
        {
            FakeGitHubConnectionNeuron.Reset();
        }
    }

    [Fact]
    public async Task McpAddWithConnectionSecretBinder_UsesCanonicalConnectionKeyWithoutEagerUnseal()
    {
        var binder = new RecordingConnectionSecretBinder();
        var throwingVault = new ThrowingSecretVault();
        var tokenRef = SecretReferences.For("local", "app.settings/default/mcp-add/token", "Bearer token", isSet: true).Reference;

        var brain = new BrainId("local");
        await using var os = TestOS.Local()
            .RegisterCatalog()
            .Register<FakeBearerMcpConnectionNeuron>();
        using var person = os.Edge.Enter(os.Edge.Person("local", "local", brain));

        var settings = await os.Install<SettingsApp>();
        settings.App.SecretVault = throwingVault;
        settings.App.ConnectionSecretBinder = binder;
        await settings.App.NavigateToAsync(new SettingsNavArgs("connections"), cancellationToken: Ct);
        await os.Idle();

        await SubmitForm(
            settings,
            SettingsControls.McpAddForm,
            new Dictionary<string, string> { ["name"] = "corp-mcp", ["url"] = "https://mcp.corp.example/api" },
            new Dictionary<string, string> { ["token"] = tokenRef });

        Assert.Equal(0, throwingVault.OpenCalls);
        Assert.NotNull(binder.LastConnectionKey);
        var parsed = ConnectionKeys.Parse(binder.LastConnectionKey!);
        Assert.Equal("local", parsed.BrainId);
        Assert.Equal("mcp", parsed.ConnectorId);
        Assert.Equal("corp-mcp", parsed.ConnectionId);
        Assert.Equal(tokenRef, binder.LastSecretRef);
        Assert.Equal("bearer-token", binder.LastAuthMethodId);
    }

    private sealed class ThrowingSecretVault : ISecretVault
    {
        public int OpenCalls { get; private set; }
        public Task<bool> HasSecretAsync(string secretName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetSecretStatusAsync(string secretName, CancellationToken cancellationToken = default) => Task.FromResult("Not set");
        public Task<string?> TryOpenSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            throw new InvalidOperationException("Vault must not be eagerly unsealed when IConnectionSecretBinder is present.");
        }
    }

    private sealed class RecordingConnectionSecretBinder : IConnectionSecretBinder
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

    private sealed class FakeOAuthDeviceFlow : IOAuthDeviceFlow
    {
        public string? LastConnectionKey { get; private set; }
        public string? LastAuthMethodId { get; private set; }

        public Task<OAuthDeviceStartResult> StartAsync(BrainId brainId, string connectionKey, string authMethodId, string? target = null, CancellationToken ct = default)
        {
            LastConnectionKey = connectionKey;
            LastAuthMethodId = authMethodId;
            return Task.FromResult(new OAuthDeviceStartResult(
                FlowId: "flow-gh-1",
                UserCode: "ABCD-1234",
                VerificationUri: "https://github.com/login/device",
                VerificationUriComplete: "https://github.com/login/device?user_code=ABCD-1234",
                ExpiresInSeconds: 900,
                IntervalSeconds: 5));
        }

        public Task<OAuthDevicePollResult> PollOnceAsync(BrainId brainId, string flowId, CancellationToken ct = default)
            => Task.FromResult(new OAuthDevicePollResult(
                Status: OAuthDevicePollStatus.Connected,
                IntervalSeconds: 5,
                Connection: new ConnectionView("github", "main", "device", ConnectionStatus.Connected, "octocat", null, DateTimeOffset.UtcNow, null),
                Error: null));
    }

    private static async Task SubmitForm(InstalledApp<SettingsApp> settings, string formKey, Dictionary<string, string> values, Dictionary<string, string>? secretRefs = null)
    {
        var submit = new Submit(values, secretRefs);
        await settings.Screen.Dispatch(new Command(formKey, "control.submit", JsonSerializer.SerializeToElement(submit, JsonSerializerOptions.Web)));
        await settings.OS.Idle();
    }
}
