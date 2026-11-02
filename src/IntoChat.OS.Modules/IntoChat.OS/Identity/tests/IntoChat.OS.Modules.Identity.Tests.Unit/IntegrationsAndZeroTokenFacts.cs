using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Authority.Connections.OAuth;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Hosting;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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
            new ModulePriceListRegistry([]));
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
    public async Task InboundWebhookRoutesResolveThreePartConnectionKeysAndReturnAccepted()
    {
        Assert.True(OAuthCallback.TryMatchWebhookPath("/integrations/github/webhook", out var provider1));
        Assert.Equal("github", provider1);

        Assert.True(OAuthCallback.TryMatchWebhookPath("/integrations/github", out var provider2));
        Assert.Equal("github", provider2);

        Assert.False(OAuthCallback.TryMatchWebhookPath("/integrations/github/registration", out _));

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/integrations/github/webhook";
        context.Request.QueryString = new QueryString("?connectionKey=local/connection/github/personal");
        context.Response.Body = new MemoryStream();

        await OAuthCallback.HandleWebhookAsync(context, "github");
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("local/connection/github/personal", doc.RootElement.GetProperty("connectionKey").GetString());
        Assert.Equal("github", doc.RootElement.GetProperty("provider").GetString());

        // Header-based 3-part ConnectionKey resolution
        var headerContext = new DefaultHttpContext();
        headerContext.Request.Method = HttpMethods.Post;
        headerContext.Request.Path = "/integrations/github";
        headerContext.Request.Headers[OAuthCallback.ConnectionKeyHeader] = "neuron://local/connection/github/work";
        Assert.True(OAuthCallback.TryResolveWebhookConnectionKey(headerContext.Request, "github", out var resolvedWork));
        Assert.Equal("local/connection/github/work", resolvedWork);

        // Query brain + connectionId resolution
        var partsContext = new DefaultHttpContext();
        partsContext.Request.QueryString = new QueryString("?brain=local&connectionId=team");
        Assert.True(OAuthCallback.TryResolveWebhookConnectionKey(partsContext.Request, "google-drive", out var resolvedTeam));
        Assert.Equal("local/connection/google-drive/team", resolvedTeam);
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
        var appCaller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/sandbox"), "ada", "local", CallerKind.App, "local/apps/sandbox");

        var vaultRequest = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "system/identity.vault/ada",
            Operation = "IVault.Resolve",
        };
        var decision = GrantRules.Evaluate(vaultRequest, null, []);
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, decision.Denial);

        var filterStage = new GrantCallFilterStage(new AllowAllPolicySource());
        var stageDecision = await filterStage.EvaluateAsync(vaultRequest, TestContext.Current.CancellationToken);
        Assert.NotNull(stageDecision);
        Assert.False(stageDecision!.Allowed);

        var exportRequest = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "local/connection/github/personal",
            Operation = "IConnection.ExportFor",
        };
        var exportDecision = GrantRules.Evaluate(exportRequest, null, []);
        Assert.NotNull(exportDecision);
        Assert.False(exportDecision!.Allowed);

        using (CallerContextScope.Scoped(appCaller))
        {
            var source = new VaultSecretValueSource(new AuthorityNeurons(null!, new EmptyServiceProvider()));
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
    public async Task LocalCloudNeuronFiltersDotFilesSecretsAndBlocksPathTraversal()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "intochat-local-cloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "readme.md"), "# Workspace Cloud", ct);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, ".env"), "SECRET_KEY=topsecret", ct);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "private.pem"), "PRIVATE KEY", ct);
            Directory.CreateDirectory(Path.Combine(tempRoot, ".git"));
            await File.WriteAllTextAsync(Path.Combine(tempRoot, ".git", "config"), "git config", ct);

            var cloud = new LocalCloudNeuron(tempRoot);
            var descriptor = await cloud.Describe(ct);
            Assert.Equal("Local Cloud", descriptor.Label);
            Assert.Equal("local-cloud", descriptor.Kind);

            var roots = await cloud.Roots(ct);
            Assert.Single(roots);
            Assert.Equal("cloud", roots[0].Id.Value);

            var writePlan = new WritePlan(
                WriteKind.WriteFile,
                Source: null,
                TargetFolder: roots[0].Id,
                Name: "notes.txt",
                Conflict: ConflictPolicy.Overwrite,
                DryRun: false,
                Inline: Encoding.UTF8.GetBytes("hello cloud"));
            await cloud.Write(writePlan, ct);

            var listed = await cloud.List(roots[0].Id, new ListQuery(), ct);
            Assert.Contains(listed.Items, i => i.Name == "readme.md");
            Assert.Contains(listed.Items, i => i.Name == "notes.txt");
            Assert.DoesNotContain(listed.Items, i => i.Name == ".env");
            Assert.DoesNotContain(listed.Items, i => i.Name == ".git");
            Assert.DoesNotContain(listed.Items, i => i.Name == "private.pem");

            var searched = await cloud.Search(new SearchQuery("readme"), ct);
            Assert.Contains(searched.Items, i => i.Name == "readme.md");

            var searchedSecrets = await cloud.Search(new SearchQuery("env"), ct);
            Assert.DoesNotContain(searchedSecrets.Items, i => i.Name == ".env");

            var searchedPem = await cloud.Search(new SearchQuery("private"), ct);
            Assert.DoesNotContain(searchedPem.Items, i => i.Name == "private.pem");

            await Assert.ThrowsAsync<Denied>(() => cloud.Read(new ItemId("../escape.txt"), null, ct));
            await Assert.ThrowsAsync<Denied>(() => cloud.Stat(new ItemId(".env"), ct));
            await Assert.ThrowsAsync<Denied>(() => cloud.Stat(new ItemId(".git/config"), ct));

            var forbiddenPlan = new WritePlan(
                WriteKind.WriteFile,
                Source: null,
                TargetFolder: roots[0].Id,
                Name: ".env.local",
                Conflict: ConflictPolicy.Overwrite,
                DryRun: false,
                Inline: "x"u8.ToArray());
            await Assert.ThrowsAsync<Denied>(() => cloud.Write(forbiddenPlan, ct));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) { Directory.Delete(tempRoot, recursive: true); }
        }
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

            var friendCaller = CapabilityCallers.Of(AppPrincipal.Id(friendApp), "ada", "local", CallerKind.App, friendApp);
            var strangerCaller = CapabilityCallers.Of(AppPrincipal.Id(strangerApp), "ada", "local", CallerKind.App, strangerApp);

            var friendRequest = new CallRequest
            {
                Caller = friendCaller,
                TargetNeuron = connectionKey,
                Operation = "IGitHub.ReadRepo",
                SemanticTypeIds = ["repo.contents"],
            };
            var friendDecision = GrantRules.Evaluate(friendRequest, null, [creatorGrant]);
            Assert.NotNull(friendDecision);
            Assert.True(friendDecision!.Allowed);

            var strangerRequest = friendRequest with { Caller = strangerCaller };
            var strangerDecision = GrantRules.Evaluate(strangerRequest, null, [creatorGrant]);
            Assert.NotNull(strangerDecision);
            Assert.False(strangerDecision!.Allowed);

            var callBindings = new ConnectionCallBindings(new NoConnectionBindings());
            var boundCall = new CallRequest
            {
                Caller = friendCaller,
                TargetNeuron = "github/" + connectionKey,
                Operation = "Read",
            };
            Assert.True(await callBindings.IsBound(boundCall, "local", TestContext.Current.CancellationToken));
        }
        finally
        {
            EdgeGrantsCache.Current.Clear();
        }
    }

    [Fact]
    public void GrantRulesFailClosedOnEmptySemanticTypeIdsAndEnforceTypedZeroTokenMatching()
    {
        var appCaller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/calculator"), "ada", "local", CallerKind.App, "local/apps/calculator");

        // 1. Foreign target with empty SemanticTypeIds must fail closed (Deny MissingGrant).
        var foreignCall = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "local/connection/postgres/main",
            Operation = "Query",
            SemanticTypeIds = [],
        };
        var foreignDecision = GrantRules.Evaluate(foreignCall, null, []);
        Assert.NotNull(foreignDecision);
        Assert.False(foreignDecision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, foreignDecision.Denial);

        // 2. Own app target with empty SemanticTypeIds is allowed.
        var ownCall = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "local/apps/calculator",
            Operation = "Compute",
            SemanticTypeIds = [],
        };
        var ownDecision = GrantRules.Evaluate(ownCall, null, []);
        Assert.NotNull(ownDecision);
        Assert.True(ownDecision!.Allowed);

        // 3. Typed zero-token guard prevents forbidden types/interfaces/methods.
        var vaultCall = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "system/identity.vault/ada",
            Operation = "Resolve",
            SemanticTypeIds = [],
        };
        var vaultDecision = GrantRules.Evaluate(vaultCall, null, []);
        Assert.NotNull(vaultDecision);
        Assert.False(vaultDecision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, vaultDecision.Denial);

        var exportCall = new CallRequest
        {
            Caller = appCaller,
            TargetNeuron = "local/connection/github/work",
            Operation = "IConnection.ExportFor",
            SemanticTypeIds = [],
        };
        var exportDecision = GrantRules.Evaluate(exportCall, null, []);
        Assert.NotNull(exportDecision);
        Assert.False(exportDecision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, exportDecision.Denial);

        // 4. Benign substring target (e.g. app named "local/apps/my-vault-helper") does not trigger false positive.
        var benignCall = new CallRequest
        {
            Caller = CapabilityCallers.Of(AppPrincipal.Id("local/apps/my-vault-helper"), "ada", "local", CallerKind.App, "local/apps/my-vault-helper"),
            TargetNeuron = "local/apps/my-vault-helper",
            Operation = "Help",
            SemanticTypeIds = [],
        };
        var benignGuardDecision = GrantRules.EvaluateZeroTokenGuard(benignCall);
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
        public ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<CallDecision?>(CallDecision.Allow());

        public ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CapabilityCaller caller, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<Grant>>([]);
    }
}
