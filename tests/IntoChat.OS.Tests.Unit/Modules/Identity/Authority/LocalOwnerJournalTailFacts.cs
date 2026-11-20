using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Kernel;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Authority;

// The debugger's journal tail is the framework's session route. The Basic operator and the Open owner carry no cookie, so the
// account session shows them to that route (and only to the framework's surface routes) as a user with the session claims.
public sealed class LocalOwnerJournalTailFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(IdentityPosture.Secured)]
    [InlineData(IdentityPosture.Open)]
    public async Task TheLocalOwnerReadsTheTailOfItsBrainAndNotOfAnotherBrain(IdentityPosture posture)
    {
        var history = new InMemorySignalHistory();
        history.Append(NeuronId.Parse("owner/ctr/default"), new Ticked(1), DateTimeOffset.UtcNow);
        var proof = posture == IdentityPosture.Open ? new PresenceProofValidator(RandomNumberGenerator.GetBytes(32)) : null;
        await using var app = await Start(posture, history, proof);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var own = Request("/brains/owner/journal/tail?since=0", posture, proof);
        using var response = await client.SendAsync(own, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var other = Request("/brains/other/journal/tail?since=0", posture, proof);
        using var refused = await client.SendAsync(other, HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (await reader.ReadLineAsync(timeout.Token) is { } line && !(line.Length == 0 && lines.Count > 0)) { lines.Add(line); }
        Assert.Contains("event: journaled-signal", lines);
        Assert.Contains(lines, line => line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("\"alias\":\"localowner.ticked\"", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task WithoutTheOperatorsCredentialTheTailIsStill401()
    {
        await using var app = await Start(IdentityPosture.Secured, new InMemorySignalHistory());
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var response = await client.GetAsync("/brains/owner/journal/tail", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutPresenceProofOpenTailIs401()
    {
        var proof = new PresenceProofValidator(RandomNumberGenerator.GetBytes(32));
        await using var app = await Start(IdentityPosture.Open, new InMemorySignalHistory(), proof);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var response = await client.GetAsync("/brains/owner/journal/tail", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OtherRoutesStillSeeNoAuthenticatedUserForTheLocalOwner()
    {
        var proof = new PresenceProofValidator(RandomNumberGenerator.GetBytes(32));
        await using var app = await Start(IdentityPosture.Open, new InMemorySignalHistory(), proof);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        using var req = Request("/probe", IdentityPosture.Open, proof);
        using var res = await client.SendAsync(req, Ct);
        res.EnsureSuccessStatusCode();
        var seen = await res.Content.ReadAsStringAsync(Ct);

        Assert.Equal("False", seen);
    }

    private static HttpRequestMessage Request(string path, IdentityPosture posture, IPresenceProof? proof = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (posture == IdentityPosture.Secured)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ops:secret")));
        }
        else if (proof is not null)
        {
            request.Headers.Add(PresenceProof.HeaderName, proof.Token);
        }
        return request;
    }

    private static async Task<WebApplication> Start(IdentityPosture posture, InMemorySignalHistory history, IPresenceProof? proof = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(LocalOwnerJournalTailFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(Options.Create(new AuthOptions { Posture = posture, Username = "ops", Password = "secret" }));
        if (proof is not null)
        {
            builder.Services.AddSingleton(proof);
        }
        builder.Services.AddKeyedSingleton<IIdentity>(Capability.Identity.V1Id, new Members(posture == IdentityPosture.Open ? "owner" : "ops"));
        builder.Services.AddSingleton<Func<BrainId, ISignalHistory>>(_ => history);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority).AddSingleton(IntoChat.OS.Tests.TestEdge.Callers);
        var app = builder.Build();
        app.UseAccountSession();
        app.MapGet("/probe", (HttpContext http) => http.User.Identity?.IsAuthenticated == true ? "True" : "False");
        app.MapIntoChatOSModules();
        await app.StartAsync(Ct);
        return app;
    }

    // identity@1 knows one member: the local owner of the brain "owner".
    private sealed class Members(string principal) : IIdentity
    {
        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken = default) => Task.FromResult<Account?>(null);
        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken = default) => Task.FromResult<Account?>(null);
        public Task EnsureAccount(Account account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken = default) => Task.FromResult<BrainProvision?>(null);
        public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BrainEntry>>([]);
        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken = default) => Task.FromResult(BrainKind.Personal);
        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken = default)
            => Task.FromResult<Member?>(brainScopeId == "owner" && principalId == principal
                ? new Member { PrincipalId = principalId, AccountId = principalId, BrainId = brainScopeId, Role = MemberRole.Owner, DisplayName = principalId, JoinedAt = DateTimeOffset.UtcNow }
                : null);

        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default) => Task.FromResult<Member?>(member);
        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

[Signal("localowner.ticked")]
public sealed record Ticked(int Value) : Signal;
