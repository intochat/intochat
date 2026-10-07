using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Platform.Integrations;
using DigitalBrain.Platform.Integrations.Accounts;
using DigitalBrain.Platform.Secrets;
using DigitalBrain.Testing.Module;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Integrations;

public sealed class AccountFacts : IDisposable
{
    private const string Owner = "owner-1";
    private const string Canary = "canary-connection-4b8e12";

    [Fact]
    public async Task InventoryListsOnlyTheAmbientBrainsConnectionsWithoutSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        foreach (var name in new[] { "one", "two" })
        {
            CallerContextStamper.Stamp(UserCaller() with { BrainId = name });
            await brain.AuthorizeCallerAsync();
            await brain.Get<IConnectionRegistry>(BrainScope.CurrentId()).Connect(new()
            { IntegrationId = "webresearch", ConnectionId = name, Value = Canary }, ct);
        }
        var provider = brain.SiloServices.GetServices<IRegistryInventoryProvider>().Single(p => p.Domain == "connections");
        foreach (var name in new[] { "one", "two" })
        {
            CallerContextStamper.Stamp(UserCaller() with { BrainId = name });
            var record = Assert.Single(await provider.Persisted(null, ct));
            Assert.Equal("webresearch/" + name, record.Display);
            Assert.Equal(BrainScope.CurrentId(), record.Key);
            Assert.Equal(record, Assert.Single(await provider.Active(null, ct)));
            Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
            Assert.DoesNotContain("secret://", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
            await brain.Get<IConnectionRegistry>(BrainScope.CurrentId()).Disconnect(name, ct);
            Assert.Empty(await provider.Persisted(null, ct));
        }
    }

    public AccountFacts() => CallerContextStamper.Stamp(UserCaller());

    public void Dispose() => Orleans.Runtime.RequestContext.Clear();

    [Fact]
    public async Task SameNamedCredentialsInDifferentRegistriesDoNotOverwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var input = new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", Value = "first" };
        var first = await brain.Get<IConnectionRegistry>("workspace-one").Connect(input, ct);
        var second = await brain.Get<IConnectionRegistry>("workspace-two").Connect(input with { Value = "second" }, ct);
        Assert.NotEqual(first.Credential.Reference, second.Credential.Reference);
        var platform = UserCaller() with { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = "connections" };
        Assert.Equal("first", await brain.Get<ISecrets>(Owner).Resolve(platform, first.Credential, ct));
        Assert.Equal("second", await brain.Get<ISecrets>(Owner).Resolve(platform, second.Credential, ct));
    }

    [Fact]
    public async Task ConnectingStoresTheCredentialAsASecretReferenceAndProbes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var accounts = brain.Get<IConnectionRegistry>(Owner);

        var account = await accounts.Connect(new ConnectConnection
        {
            IntegrationId = "webresearch",
            ConnectionId = "research",
            Label = "Research key",
            Value = Canary,
        }, ct);

        Assert.Equal(ConnectionStatus.Connected, account.Status);
        Assert.Equal("webresearch", account.IntegrationId);
        Assert.StartsWith("secret://", account.Credential.Reference, StringComparison.Ordinal);
        Assert.Equal(Owner, account.WorkspaceId);
        Assert.Single(await accounts.List(ct));
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(account), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProbeTracksExpiredThenFailingStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var scripted = new ScriptedProbe(ConnectionProbeOutcome.Expired, ConnectionProbeOutcome.Failing);
        await using var brain = await ModuleTest.Create().WithModule<ConnectionTestModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IConnectionProbe>(scripted))
            .StartAsync(ct);
        var accounts = brain.Get<IConnectionRegistry>(Owner);

        var connected = await accounts.Connect(new ConnectConnection
        {
            IntegrationId = "supabase",
            ConnectionId = "db",
            Value = "Host=db;Database=app;Username=reader;Password=pw",
        }, ct);
        Assert.Equal(ConnectionStatus.Expired, connected.Status);

        var probed = await accounts.Probe("db", ct);
        Assert.Equal(ConnectionStatus.Failing, probed.Status);
        Assert.NotNull(probed.LastProbedAt);
    }

    [Fact]
    public async Task DisconnectRemovesTheAccountAndPublishesASignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var accounts = brain.Get<IConnectionRegistry>(Owner);
        await using var disconnected = await brain.Observe<ConnectionDisconnected>(accounts, ct);

        await accounts.Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", Value = "oauth-token" }, ct);
        await accounts.Disconnect("mail", ct);

        Assert.Empty(await accounts.List(ct));
        Assert.Equal("mail", (await disconnected.NextAsync(ct: ct)).ConnectionId);
    }

    [Fact]
    public async Task ProbingAnUnknownAccountFails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);

        await Assert.ThrowsAsync<ConnectionNotConfiguredException>(() => brain.Get<IConnectionRegistry>(Owner).Probe("missing", ct));
    }

    [Fact]
    public async Task AnEmptyIntegrationIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnectionRegistry>(Owner)
            .Connect(new ConnectConnection { IntegrationId = "", ConnectionId = "x", Value = "v" }, ct));
    }

    [Fact]
    public void TheAccountsContractIsPlatformOnly()
    {
        Assert.True(PlatformOnlyAttribute.AppliesTo(typeof(IConnectionRegistry)));
    }

    [Fact]
    public async Task AnAppStampedAmbientCallerCannotConnectProbeOrDisconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var accounts = brain.Get<IConnectionRegistry>(Owner);
        await accounts.Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", Value = "v" }, ct);

        CallerContextStamper.Stamp(UserCaller() with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy });

        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.Connect(
            new ConnectConnection { IntegrationId = "gmail", ConnectionId = "other", Value = "v" }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.Probe("mail", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.Disconnect("mail", ct));
        CallerContextStamper.Stamp(UserCaller());
        Assert.Single(await accounts.List(ct));
    }

    [Theory]
    [InlineData("secret://bob/other-secret")]
    [InlineData("secret://:integrations/openai.ApiKey")]
    [InlineData("secret://")]
    public async Task ASecretReferenceIntoAnyOtherOwnersVaultIsRefusedWithoutNamingIt(string reference)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);

        var refusal = await Assert.ThrowsAsync<ArgumentException>(() => brain.Get<IConnectionRegistry>(Owner)
            .Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", SecretReference = reference }, ct));

        Assert.DoesNotContain(reference, refusal.Message, StringComparison.Ordinal);
        Assert.Empty(await brain.Get<IConnectionRegistry>(Owner).List(ct));
    }

    [Fact]
    public async Task ASecretReferenceIntoTheCallersOwnVaultIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var own = await brain.Get<ISecrets>(Owner).Set(UserCaller(), "mine", "Mine", "own-value", ct);

        var account = await brain.Get<IConnectionRegistry>(Owner)
            .Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", SecretReference = own.Reference }, ct);

        Assert.Equal(own.Reference, account.Credential.Reference);
    }

    [Fact]
    public async Task TheCallerIsTheAmbientStampAndNothingWithoutOneIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);

        var account = await brain.Get<IConnectionRegistry>("workspace-one")
            .Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail", Value = "mine" }, ct);

        Assert.Equal(Owner, account.Credential.OwnerOf());
        Orleans.Runtime.RequestContext.Clear();
        await Assert.ThrowsAsync<UntrustedCallerException>(() => brain.Get<IConnectionRegistry>("workspace-one")
            .Connect(new ConnectConnection { IntegrationId = "gmail", ConnectionId = "mail2", Value = "v" }, ct));
        CallerContextStamper.Stamp(UserCaller());
        Assert.Equal("mail", Assert.Single(await brain.Get<IConnectionRegistry>("workspace-one").List(ct)).Id);
    }

    [Fact]
    public void CapabilitiesAreServedUnderTheBrainScopedIntegrationsRoute()
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(IGrainFactory), _ => null!);
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        IEndpointRouteBuilder app = builder.Build();
        DigitalBrain.Platform.PlatformHosting.MapDigitalBrainPlatform(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!).ToArray();

        Assert.Contains("/brains/{brainId}/integrations/capabilities", routes);
    }

    [Fact]
    public void AccountsAreServedOnlyUnderTheBrainScopedAccountsRoute()
    {
        var builder = WebApplication.CreateBuilder();
        DigitalBrain.Testing.TestLogging.Apply(builder.Configuration);
        builder.Services.AddSingleton(typeof(IGrainFactory), _ => null!);
        builder.Services.AddSingleton(typeof(IDigitalBrain), _ => null!);
        IEndpointRouteBuilder app = builder.Build();
        DigitalBrain.Platform.PlatformHosting.MapDigitalBrainPlatform(app);

        var routes = app.DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText!).ToArray();

        Assert.Contains("/brains/{brainId}/connections/connect", routes);
        Assert.Contains("/brains/{brainId}/connections/probe", routes);
        Assert.Contains("/brains/{brainId}/connections/disconnect", routes);
        Assert.Contains("/brains/{brainId}/connections/requests", routes);
        Assert.DoesNotContain(routes, route => route.StartsWith("/connections", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ConnectionStatus.Connected, "Configured")]
    [InlineData(ConnectionStatus.Expired, "Expired")]
    [InlineData(ConnectionStatus.Failing, "Unavailable")]
    public void StoredCredentialsDoNotImplyVerification(ConnectionStatus status, string expected)
        => Assert.Equal(expected, ConnectionEndpoints.Status(status));

    [Fact]
    public void PackageAccountsIncludeOnlyRecordsFiledUnderTheBrain()
    {
        var scope = BrainScope.Create("alice", "one");
        static Connection Account(string id, string workspace, string owner) => new()
        {
            Id = id,
            IntegrationId = "gmail",
            WorkspaceId = workspace,
            Status = ConnectionStatus.Connected,
            Credential = SecretReferences.For(owner, id, id, true),
        };
        var result = ScopedConnections.Visible(scope, [Account("new", "one", "alice"), Account("wrong-workspace", "two", "alice")]);
        Assert.Equal(["new"], result.Select(account => account.Id));
    }

    private static Task<ModuleBrain> StartAsync(CancellationToken cancellationToken)
        => ModuleTest.Create().WithModule<ConnectionTestModule>().StartAsync(cancellationToken);

    private static CallerContext UserCaller() => new()
    {
        PrincipalId = Owner,
        AccountId = Owner,
        BrainId = Owner,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };
}

internal sealed class ScriptedProbe(params ConnectionProbeOutcome[] outcomes) : IConnectionProbe
{
    private int _index;

    public Task<ConnectionProbeResult> ProbeAsync(string source, string credential, CancellationToken cancellationToken = default)
    {
        var outcome = outcomes[Math.Min(_index++, outcomes.Length - 1)];
        return Task.FromResult(new ConnectionProbeResult(outcome, "scripted"));
    }
}

public sealed class ConnectionTestModule() : Module(new ModuleId("connection-test"))
{
    public static ConnectorDefinition[] Connectors => [
        new("gmail", "Gmail", ["api-key"], []),
        new("supabase", "Supabase", ["connection-string"], []),
        new("webresearch", "Research", ["api-key"], []),
    ];
    public override void Configure(Orleans.Hosting.ISiloBuilder silo) { }
}
