using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Connections.Surface;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DbIdentity = DigitalBrain.Capabilities.Identity;
using IdentityPosture = IntoChat.OS.Connections.Identity.Configuration.IdentityPosture;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class PresenceProofAndEdgeAuthFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void PresenceProofValidator_MatchesBase64UrlBase64AndHexOfSame32ByteSecret_AndRejectsForgery()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var validator = new PresenceProofValidator(raw);

        Assert.True(validator.MatchesHeader(validator.Token));
        Assert.True(validator.MatchesHeader(Convert.ToBase64String(raw)));
        Assert.True(validator.MatchesHeader(Convert.ToHexString(raw)));
        Assert.True(validator.MatchesHeader(Convert.ToHexStringLower(raw)));

        Assert.False(validator.MatchesHeader(null));
        Assert.False(validator.MatchesHeader(string.Empty));
        Assert.False(validator.MatchesHeader("forged-presence-token-000000000000000000000000"));

        var mutated = raw.ToArray();
        mutated[0] ^= 0xFF;
        Assert.False(validator.MatchesHeader(Convert.ToBase64String(mutated)));
    }

    [Fact]
    public void PresenceProofValidator_LoadsSecretFromPresenceFile_UnderConfiguredHome()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "intochat-presence-unit-" + Guid.NewGuid().ToString("N"));
        var runDir = Path.Combine(tempHome, "run");
        Directory.CreateDirectory(runDir);
        try
        {
            var raw = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(Path.Combine(runDir, "presence"), raw);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IntoChatOS:Home"] = tempHome,
                    [PresenceProofValidator.EnvironmentVariable] = null,
                })
                .Build();

            var validator = new PresenceProofValidator(config);
            Assert.True(validator.MatchesHeader(Convert.ToBase64String(raw)));
            Assert.True(validator.MatchesHeader(validator.Token));
        }
        finally
        {
            if (Directory.Exists(tempHome))
            {
                Directory.Delete(tempHome, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OpenPosture_WithoutPresenceProof_StampsLocalNonPersonApp_AndWithValidHeaderStampsPerson()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var proof = new PresenceProofValidator(raw);

        await using var app = await StartEdgeTestAppAsync(IdentityPosture.Open, proof);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // 1. Unproved loopback request -> App("local"), NOT Actor.Person!
        var unproved = await client.GetFromJsonAsync<CallerProbe>("/probe/caller", Ct);
        Assert.NotNull(unproved);
        Assert.Equal(CallerKind.App.ToString(), unproved.Kind);
        Assert.Equal(AccountSession.LocalAppId, unproved.AppId);
        Assert.False(unproved.IsActorPerson);

        // 2. Forged X-IntoChat-Presence header -> 401 Unauthorized (never silently falls back to local!)
        using (var forgedReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller"))
        {
            forgedReq.Headers.Add(PresenceProofValidator.HeaderName, "stale-or-forged-token");
            using var forgedRes = await client.SendAsync(forgedReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedRes.StatusCode);
        }

        // 3. Valid X-IntoChat-Presence header -> CallerKind.User / Actor.Person!
        using var provedReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller");
        provedReq.Headers.Add(PresenceProofValidator.HeaderName, proof.Token);
        using var provedRes = await client.SendAsync(provedReq, Ct);
        Assert.Equal(HttpStatusCode.OK, provedRes.StatusCode);
        var proved = await provedRes.Content.ReadFromJsonAsync<CallerProbe>(cancellationToken: Ct);
        Assert.NotNull(proved);
        Assert.Equal(CallerKind.User.ToString(), proved.Kind);
        Assert.Null(proved.AppId);
        Assert.True(proved.IsActorPerson);
    }

    [Fact]
    public async Task LocalPresenceExchange_MintsStrictHttpOnlyCookie_AndRejectsCrossOriginRequests()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var proof = new PresenceProofValidator(raw);

        await using var app = await StartEdgeTestAppAsync(IdentityPosture.Open, proof);
        var baseUri = new Uri(app.Urls.First());
        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = baseUri };

        // Invalid token -> 401
        using (var badRes = await client.PostAsJsonAsync(PresenceProofValidator.ExchangePath, new { token = "wrong" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, badRes.StatusCode);
        }

        // Cross-origin exchange attempt -> 401 even with valid token
        using (var crossReq = new HttpRequestMessage(HttpMethod.Post, PresenceProofValidator.ExchangePath)
        {
            Content = JsonContent.Create(new { token = proof.Token }),
        })
        {
            crossReq.Headers.Add("Origin", "http://evil.example:8080");
            using var crossRes = await client.SendAsync(crossReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, crossRes.StatusCode);
        }

        // Same-origin exchange with valid token -> 200 + HttpOnly; SameSite=Strict cookie
        string? setCookieHeader;
        using (var okReq = new HttpRequestMessage(HttpMethod.Post, PresenceProofValidator.ExchangePath)
        {
            Content = JsonContent.Create(new { token = proof.Token }),
        })
        {
            okReq.Headers.Add("Origin", $"{baseUri.Scheme}://{baseUri.Authority}");
            using var okRes = await client.SendAsync(okReq, Ct);
            Assert.Equal(HttpStatusCode.OK, okRes.StatusCode);
            Assert.True(okRes.Headers.TryGetValues("Set-Cookie", out var cookies));
            setCookieHeader = cookies.Single(c => c.StartsWith(PresenceProofValidator.CookieName + "=", StringComparison.Ordinal));
        }

        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookieHeader, StringComparison.OrdinalIgnoreCase);

        var cookiePair = setCookieHeader.Split(';', 2)[0];

        // Request carrying the exchanged cookie from same origin -> Actor.Person
        using (var cookieReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller"))
        {
            cookieReq.Headers.Add("Cookie", cookiePair);
            cookieReq.Headers.Add("Origin", $"{baseUri.Scheme}://{baseUri.Authority}");
            using var cookieRes = await client.SendAsync(cookieReq, Ct);
            Assert.Equal(HttpStatusCode.OK, cookieRes.StatusCode);
            var caller = await cookieRes.Content.ReadFromJsonAsync<CallerProbe>(cancellationToken: Ct);
            Assert.NotNull(caller);
            Assert.True(caller.IsActorPerson);
        }

        // Request carrying the exchanged cookie with cross-site header -> 401
        using var csrfReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller");
        csrfReq.Headers.Add("Cookie", cookiePair);
        csrfReq.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var csrfRes = await client.SendAsync(csrfReq, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, csrfRes.StatusCode);
    }

    [Fact]
    public async Task AccountSessionIssuer_FallsBackToLocalCookie_WhenRefusingISessionsExistsWithoutSessionPort()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        // Register a refusing ISessions WITHOUT IIdentitySessionPort (exact B02 local-silo scenario)
        services.AddSingleton<DbIdentity.ISessions>(new RefusingSessions());
        services.AddSingleton<ISessionIssuer, AccountSessionIssuer>();

        await using var provider = services.BuildServiceProvider();
        var issuer = provider.GetRequiredService<ISessionIssuer>();

        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };

        var request = new DbIdentity.SessionRequest
        {
            Member = new DbIdentity.Member
            {
                PrincipalId = "alice",
                AccountId = "alice",
                BrainId = "owner",
                Role = DbIdentity.MemberRole.Owner,
                DisplayName = "Alice",
                JoinedAt = DateTimeOffset.UtcNow,
            },
            Claims = new Dictionary<string, string>(),
        };

        var issued = await issuer.IssueAsync(context, request, Ct);
        Assert.False(string.IsNullOrWhiteSpace(issued.SessionId));
    }

    [Fact]
    public async Task McpCaller_AlwaysWrapsResolvedCallerAsAssistantAgent_EvenWhenHttpRequestHasPresenceProof()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var proof = new PresenceProofValidator(raw);
        var services = new ServiceCollection();
        services.AddSingleton<IPresenceProof>(proof);
        using var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Headers[PresenceProofValidator.HeaderName] = proof.Token;

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var auth = Options.Create(new AuthOptions { Posture = IdentityPosture.Open });
        await using var os = DigitalBrain.Testing.TestOS.Local();
        var mcpCaller = new McpCaller(accessor, auth, os);

        var stamped = mcpCaller.Stamp(NeuronSurfaceTools.CallName);
        Assert.Equal(CallerKind.Agent, stamped.Kind);
        Assert.Equal(CapabilityCallers.AssistantApp, stamped.AppId);
        Assert.Equal("mcp", stamped.ConversationId);
        Assert.IsType<Actor.Agent>(stamped.ToCaller().Actor);
    }

    [Fact]
    public async Task GrantPolicySource_AllowsLocalAppCallerInOpenPosture_WhileZeroTokenGuardStillDeniesVault()
    {
        var identity = new StubIdentityClient();
        var mirror = new AuthorityMirror();
        var auth = Options.Create(new AuthOptions { Posture = IdentityPosture.Open });
        var source = new GrainGrantPolicySource(identity, mirror, auth);
        var stage = new GrantCallFilterStage(source);

        var localCaller = (CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin)
            with
        { AppId = AccountSession.LocalAppId }).ToCaller();

        var uiDecision = await stage.EvaluateAsync(new CallRequest
        {
            Caller = localCaller,
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = "owner",
            EnforceTarget = true,
            SemanticTypeIds = ["ui.view"],
        }, Ct);
        Assert.Null(uiDecision);

        var vaultDecision = await stage.EvaluateAsync(new CallRequest
        {
            Caller = localCaller,
            TargetNeuron = "identity.vault/owner/main",
            Operation = "IVault.Resolve",
            TargetScope = "owner",
            EnforceTarget = true,
            SemanticTypeIds = ["identity.vault"],
        }, Ct);
        Assert.NotNull(vaultDecision);
        Assert.False(vaultDecision.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, vaultDecision.Denial);
    }

    private static async Task<WebApplication> StartEdgeTestAppAsync(IdentityPosture posture, IPresenceProof proof)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(PresenceProofAndEdgeAuthFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthOptions.PostureKey] = posture.ToString(),
        });
        builder.Services.AddSingleton<IPresenceProof>(proof);
        builder.Services.AddSingleton<IIdentityClient>(new StubIdentityClient());
        builder.Services.AddIdentity();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAccountSession();
        IdentityEndpoints.Map(app);
        app.MapGet("/probe/caller", () =>
        {
            var caller = RuntimeCallerContext.Require();
            var actor = caller.ToCaller().Actor;
            return Results.Ok(new CallerProbe(
                caller.PrincipalId,
                caller.AccountId,
                caller.BrainId,
                caller.Kind.ToString(),
                caller.AppId,
                actor is Actor.Person));
        });

        await app.StartAsync(Ct);
        return app;
    }

    private sealed record CallerProbe(
        string PrincipalId,
        string AccountId,
        string BrainId,
        string Kind,
        string? AppId,
        bool IsActorPerson);

    private sealed class RefusingSessions : DbIdentity.ISessions
    {
        public Task<DbIdentity.SessionIssued> Issue(DbIdentity.SessionRequest request, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("PrivilegeDenied: module 'auth' has no grant.");

        public Task<DbIdentity.Member?> Validate(string sessionId, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("PrivilegeDenied: module 'auth' has no grant.");

        public Task End(string sessionId, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("PrivilegeDenied: module 'auth' has no grant.");
    }

    private sealed class StubIdentityClient : IIdentityClient
    {
        public Task<Account?> ReadAccount(string accountId) => Task.FromResult<Account?>(null);
        public Task<Account?> OwnerOfBrain(string brainId) => Task.FromResult<Account?>(null);
        public Task EnsureAccount(Account account) => Task.CompletedTask;
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId) => Task.FromResult(true);
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId) => Task.FromResult<BrainProvision?>(null);
        public Task<BrainEntry[]> BrainsOf(string principalId) => Task.FromResult<BrainEntry[]>([]);
        public Task<BrainKind> KindOf(string brainScopeId) => Task.FromResult(BrainKind.Personal);
        public Task<bool> BrainExists(string brainScopeId) => Task.FromResult(true);
        public Task<Member?> Membership(string brainScopeId, string principalId) => Task.FromResult<Member?>(new Member
        {
            PrincipalId = principalId,
            AccountId = AccountSession.DefaultLogin,
            BrainId = AccountSession.DefaultLogin,
            Role = MemberRole.Owner,
            DisplayName = principalId,
            JoinedAt = DateTimeOffset.UtcNow,
        });
        public Task<Member?> EnsureMember(Member member) => Task.FromResult<Member?>(member);
        public Task RemoveMember(string brainScopeId, string accountId) => Task.CompletedTask;
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => Task.CompletedTask;
        public Task<CallDecision?> Authorize(CallRequest request) => Task.FromResult<CallDecision?>(CallDecision.Allow());
        public Task<Grant[]> ListGrants(string brainScopeId) => Task.FromResult<Grant[]>([]);
        public Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant) => Task.FromResult(grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => Task.FromResult(grants);
        public Task RevokeAsOwner(CapabilityCaller caller, string grantId) => Task.CompletedTask;
        public Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode) => Task.CompletedTask;
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null) => throw new NotSupportedException();
    }
}
