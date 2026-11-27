using DigitalBrain;
using System.Text;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Modules.LocalDisk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Connections;
using IntoChat.OS.Modules.Identity.Connections.OAuth;
using IntoChat.OS.Modules.Identity.Grants;
using IntoChat.OS.Modules.Identity.Secrets;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class IntegrationsAndZeroTokenFacts
{
    [Fact]
    public void OAuthCallbackUriAndRoutingUseUnifiedIntegrationsProviderPathAndBackwardsCompatiblePaths()
    {
        var options = Options.Create(new ConnectionsOptions
        {
            PublicOrigin = new Uri("https://os.intochat.test"),
        });
        var requests = new OAuthLoginRequests(options, TimeProvider.System);
        var exchange = new OAuthTokenExchange(
            [],
            new AuthorityNeurons(null!, new EmptyServiceProvider()),
            options,
            TimeProvider.System,
            new FakeHttpClientFactory(),
            new ModulePriceListRegistry([]),
            IntoChat.OS.Tests.TestEdge.Authority);
        var flow = new OAuthLoginFlow(requests, exchange, options);

        Assert.Equal("https://os.intochat.test/integrations/github/oauth/callback", flow.CallbackUri("github").AbsoluteUri);

        var unconfiguredOptions = Options.Create(new ConnectionsOptions());
        var fallbackFlow = new OAuthLoginFlow(new OAuthLoginRequests(unconfiguredOptions, TimeProvider.System), exchange, unconfiguredOptions);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("localhost", 5080);
        Assert.Equal("http://localhost:5080/integrations/github/oauth/callback", fallbackFlow.CallbackUri("github", httpContext.Request).AbsoluteUri);

        Assert.True(OAuthCallback.TryMatchCallbackPath("/integrations/oauth/callback", out var p0));
        Assert.Equal("", p0);

        Assert.True(OAuthCallback.TryMatchCallbackPath("/integrations/github/oauth/callback", out var p1));
        Assert.Equal("github", p1);

        Assert.True(OAuthCallback.TryMatchCallbackPath("/integrations/google-drive/callback", out var p2));
        Assert.Equal("google-drive", p2);

        Assert.True(OAuthCallback.TryMatchCallbackPath("/connections/oauth/slack/callback", out var p3));
        Assert.Equal("slack", p3);

        Assert.False(OAuthCallback.TryMatchCallbackPath("/integrations/github/registration", out _));
    }

    [Fact]
    public async Task UnsignedIntegrationsWebhookRouteIsNotAcceptedAndReturns404()
    {
        var app = new ApplicationBuilder(new EmptyServiceProvider());
        new OAuthCallback().Map(app);
        app.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        var pipeline = app.Build();

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/integrations/other/webhook";
        context.Request.QueryString = new QueryString("?connectionKey=local/connection/other/personal");
        context.Response.Body = new MemoryStream();

        await pipeline(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task MultiNeuronConnectionsPerBrainAreSupportedAndDisambiguatedByThreePartConnectionKey()
    {
        var tracked = new[]
        {
            ConnectionKeys.For("local", "github", "personal"),
            ConnectionKeys.For("local", "github", "work"),
            ConnectionKeys.For("local", "google-drive", "work"),
        };

        Assert.Equal("neuron://local/connection/github/personal", ConnectionKeys.NeuronUri(tracked[0]));
        Assert.Equal("neuron://local/connection/github/work", ConnectionKeys.NeuronUri(tracked[1]));
        Assert.Equal("neuron://local/connection/google-drive/work", ConnectionKeys.NeuronUri(tracked[2]));

        Assert.Null(await ConnectionLogin.TakenError(null!, "work", "github"));
        Assert.False(await ConnectionEndpoints.IsTakenByAnotherConnector(null!, "work", "github"));

        var personalMatch = ConnectionEndpoints.FilterTrackedKeys(tracked, "local", "personal");
        Assert.Single(personalMatch, tracked[0]);

        var disambiguatedByConnector = ConnectionEndpoints.FilterTrackedKeys(tracked, "local", "work", connectorId: "github");
        Assert.Single(disambiguatedByConnector, tracked[1]);

        var disambiguatedByFullKey = ConnectionEndpoints.FilterTrackedKeys(tracked, "local", "work", connectionKey: "neuron://local/connection/google-drive/work");
        Assert.Single(disambiguatedByFullKey, tracked[2]);
    }

    [Fact]
    public async Task ZeroTokenAuthEnforcementDeniesDirectSecretAccessFromSandboxAndAppPrincipals()
    {
        var appCaller = IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/sandbox", new Actor.Person("ada", "ada"), new BrainId("local"));
        var appEdge = appCaller;

        var vaultRequest = new CallRequest
        {
            TargetNeuron = "system/identity.vault/ada",
            Operation = "IVault.Resolve",
        };
        var decision = GrantRules.Evaluate(appEdge, vaultRequest, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, decision.Denial);

        var filterStage = new GrantCallFilterStage(new AllowAllPolicySource());
        var stageDecision = await filterStage.EvaluateAsync(appEdge, vaultRequest, TestContext.Current.CancellationToken);
        Assert.NotNull(stageDecision);
        Assert.False(stageDecision!.Allowed);

        var exportRequest = new CallRequest
        {
            TargetNeuron = "local/connection/github/personal",
            Operation = "IConnection.ExportFor",
        };
        var exportDecision = GrantRules.Evaluate(appEdge, exportRequest, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(exportDecision);
        Assert.False(exportDecision!.Allowed);

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(appCaller))
        {
            var source = new DigitalBrain.Identity.Local.Vault.CachingSecretValueSource(
                (_, _) => ValueTask.FromResult<IReadOnlyList<string>>([]),
                IntoChat.OS.Tests.TestEdge.Callers,
                extraAppCheck: GrantRules.IsAppCaller);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                async () => await source.CurrentValues(TestContext.Current.CancellationToken));
        }

        var credential = new Credential("gho_secret_access_token_123", "ghr_secret_refresh_token_456");
        Assert.Throws<InvalidOperationException>(ConnectionNeuron<ConnectionState>.AssertZeroTokenReturnType<Credential>);
        Assert.Throws<InvalidOperationException>(ConnectionNeuron<ConnectionState>.AssertZeroTokenReturnType<OAuthGrant>);
        Assert.Throws<InvalidOperationException>(() => ConnectionNeuron<ConnectionState>.AssertZeroTokenReturn("leaked: gho_secret_access_token_123", credential));
        ConnectionNeuron<ConnectionState>.AssertZeroTokenReturn("repo: intochat/os (200 OK)", credential);
    }

    [Fact]
    public async Task EdgeGrantsCacheAllowsTrustedFriendAppsThroughGrantRulesAndConnectionBindings()
    {
        EdgeGrantsCache.Current.Clear();
        try
        {
            var friendApp = "local/apps/creator";
            var strangerApp = "local/apps/stranger";
            var connectionKey = ConnectionKeys.For("local", "github", "personal");

            var creatorGrant = Grant.Tuple("brain:local", $"app:{friendApp}", connectionKey, "repo.contents");
            var boundGrant = Grant.Tuple("brain:local", $"app:{friendApp}", connectionKey, $"use-via:{friendApp}");
            EdgeGrantsCache.Current.AddGrant("local", boundGrant);

            var friendCaller = IntoChat.OS.Tests.TestEdge.Authority.App(friendApp, new Actor.Person("ada", "ada"), new BrainId("local"));
            var strangerCaller = IntoChat.OS.Tests.TestEdge.Authority.App(strangerApp, new Actor.Person("ada", "ada"), new BrainId("local"));

            var request = new CallRequest
            {
                TargetNeuron = connectionKey,
                Operation = "IGitHub.ReadRepo",
                SemanticTypeIds = ["repo.contents"],
            };
            var friendDecision = GrantRules.Evaluate(friendCaller, request, null, [creatorGrant], PrincipalSet.For(friendCaller));
            Assert.NotNull(friendDecision);
            Assert.True(friendDecision!.Allowed);

            var strangerDecision = GrantRules.Evaluate(strangerCaller, request, null, [creatorGrant], PrincipalSet.For(strangerCaller));
            Assert.NotNull(strangerDecision);
            Assert.False(strangerDecision!.Allowed);
        }
        finally
        {
            EdgeGrantsCache.Current.Clear();
        }
    }

    [Fact]
    public void GrantRulesFailClosedOnEmptySemanticTypeIdsAndEnforceTypedZeroTokenMatching()
    {
        var appCaller = IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/calculator", new Actor.Person("ada", "ada"), new BrainId("local"));
        var appEdge = appCaller;

        // 1. Foreign target with empty SemanticTypeIds must fail closed (Deny MissingGrant).
        var foreignCall = new CallRequest
        {
            TargetNeuron = "local/connection/postgres/main",
            Operation = "Query",
            SemanticTypeIds = [],
        };
        var foreignDecision = GrantRules.Evaluate(appEdge, foreignCall, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(foreignDecision);
        Assert.False(foreignDecision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, foreignDecision.Denial);

        // 2. Own app target with empty SemanticTypeIds is allowed.
        var ownCall = new CallRequest
        {
            TargetNeuron = "local/apps/calculator",
            Operation = "Compute",
            SemanticTypeIds = [],
        };
        var ownDecision = GrantRules.Evaluate(appEdge, ownCall, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(ownDecision);
        Assert.True(ownDecision!.Allowed);

        // 3. Typed zero-token guard prevents forbidden types/interfaces/methods.
        var vaultCall = new CallRequest
        {
            TargetNeuron = "system/identity.vault/ada",
            Operation = "Resolve",
            SemanticTypeIds = [],
        };
        var vaultDecision = GrantRules.Evaluate(appEdge, vaultCall, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(vaultDecision);
        Assert.False(vaultDecision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, vaultDecision.Denial);

        var exportCall = new CallRequest
        {
            TargetNeuron = "local/connection/github/work",
            Operation = "IConnection.ExportFor",
            SemanticTypeIds = [],
        };
        var exportDecision = GrantRules.Evaluate(appEdge, exportCall, null, [], PrincipalSet.For(appCaller));
        Assert.NotNull(exportDecision);
        Assert.False(exportDecision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, exportDecision.Denial);

        // 4. Benign substring target (e.g. app named "local/apps/my-vault-helper") does not trigger false positive.
        var benignCall = new CallRequest
        {
            TargetNeuron = "local/apps/my-vault-helper",
            Operation = "Help",
            SemanticTypeIds = [],
        };
        var benignGuardDecision = GrantRules.EvaluateZeroTokenGuard(
            IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/my-vault-helper", new Actor.Person("ada", "ada"), new BrainId("local")),
            benignCall);
        Assert.Null(benignGuardDecision);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class AllowAllPolicySource : IGrantPolicySource
    {
        public ValueTask<CallDecision?> AuthorizeAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
    }
}
