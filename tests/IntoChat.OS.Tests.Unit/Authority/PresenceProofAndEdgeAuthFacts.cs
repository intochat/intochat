using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DbIdentity = DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Surface;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class PresenceProofAndEdgeAuthFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The same host TestEdge.Authority mints for, so the ambient the edge enters is the one the probe reads.
    private static readonly DigitalBrain.Testing.TestOS Host = DigitalBrain.Testing.TestOS.Local();

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
    public async Task OpenPosture_WithoutPresenceProof_Returns401Unauthorized_AndWithValidHeaderStampsPerson()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var proof = new PresenceProofValidator(raw);

        await using var app = await StartEdgeTestAppAsync(IdentityPosture.Open, proof);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // 1. Unproved loopback request -> 401 Unauthorized (never silently falls back to local!)
        using var unprovedRes = await client.GetAsync("/probe/caller", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unprovedRes.StatusCode);

        // 2. Forged X-IntoChat-Presence header -> 401 Unauthorized
        using (var forgedReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller"))
        {
            forgedReq.Headers.Add(PresenceProofValidator.HeaderName, "stale-or-forged-token");
            using var forgedRes = await client.SendAsync(forgedReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedRes.StatusCode);
        }

        // 3. Valid X-IntoChat-Presence header -> Actor.Person
        using var provedReq = new HttpRequestMessage(HttpMethod.Get, "/probe/caller");
        provedReq.Headers.Add(PresenceProofValidator.HeaderName, proof.Token);
        using var provedRes = await client.SendAsync(provedReq, Ct);
        Assert.Equal(HttpStatusCode.OK, provedRes.StatusCode);
        var proved = await provedRes.Content.ReadFromJsonAsync<CallerProbe>(cancellationToken: Ct);
        Assert.NotNull(proved);
        Assert.Equal(nameof(Actor.Person), proved.Kind);
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
    public async Task GrantPolicySource_AllowsProvenPersonInOpenPosture_WhileZeroTokenGuardStillDeniesVault()
    {
        var identity = new StubIdentityClient();
        var mirror = new AuthorityMirror();
        var auth = Options.Create(new AuthOptions { Posture = IdentityPosture.Open });
        var source = new GrainGrantPolicySource(identity, mirror, auth);
        var stage = new GrantCallFilterStage(source);

        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        var owner = new DigitalBrain.BrainId(AccountSession.DefaultLogin);
        var personCaller = edge.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin, owner);
        var uiDecision = await stage.EvaluateAsync(personCaller, new CallRequest
        {
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = "owner",
            EnforceTarget = true,
            SemanticTypeIds = ["ui.view"],
        }, Ct);
        Assert.NotNull(uiDecision);
        Assert.True(uiDecision.Allowed);

        var untrustedAppCaller = edge.App("untrusted-app", new Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin), owner);

        var vaultDecision = await stage.EvaluateAsync(untrustedAppCaller, new CallRequest
        {
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
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority);
        builder.Services.AddSingleton(Host.Callers);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAccountSession();
        IdentityEndpoints.Map(app);
        app.MapGet("/probe/caller", (ICallerAmbient callers) =>
        {
            var caller = callers.Current ?? throw new UnauthorizedAccessException("no caller");
            return Results.Ok(new CallerProbe(
                caller.For?.Principal ?? "",
                caller.For?.Account ?? "",
                caller.Brain.Value,
                caller.Actor.GetType().Name,
                caller.Actor is Actor.Person));
        });

        await app.StartAsync(Ct);
        return app;
    }

    private sealed record CallerProbe(
        string PrincipalId,
        string AccountId,
        string BrainId,
        string Kind,
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
        public Task<CallDecision?> Authorize(Caller? caller, CallRequest request) => Task.FromResult<CallDecision?>(CallDecision.Allow());
        public Task<Grant[]> ListGrants(string brainScopeId) => Task.FromResult<Grant[]>([]);
        public Task<Grant> GrantAsOwner(string brainId, Grant grant) => Task.FromResult(grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => Task.FromResult(grants);
        public Task RevokeAsOwner(string brainId, string grantId) => Task.CompletedTask;
        public Task RevokeAsOwner(string brainId, string appId, string semanticTypeId, GrantMode mode) => Task.CompletedTask;
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null) => throw new NotSupportedException();
    }
}
