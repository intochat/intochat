using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Identity;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using IdentityPosture = IntoChat.OS.Connections.Identity.Configuration.IdentityPosture;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class EdgePresenceSecurityFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SiloAccessCallFilter_RequiresConstructorInjection_HasNoServiceProviderConstructor()
    {
        var ctors = typeof(SiloAccessCallFilter).GetConstructors();
        var ctor = Assert.Single(ctors);
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IServiceProvider), paramTypes);
        Assert.Equal(typeof(IEnumerable<ICallFilterStage>), paramTypes[0]);
    }

    [Fact]
    public async Task SiloAccessCallFilter_FailsClosed_WhenStagesEmptyOrReturnNull()
    {
        var personCaller = IntoChat.OS.Tests.TestEdge.From(CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin));
        var ambient = new FixedCallerAmbient(personCaller);
        var invocation = new RuntimeInvocation("app.view", "DigitalBrain.UI.IView", "main", "Read", []);
        var callContext = new FakeGrainCallContext(GrainId.Create("neuron", "owner/app.view/main"), invocation);

        var emptyStagesFilter = new SiloAccessCallFilter([], ambient, new AccessOptions { UnstampedEdgeIsKernel = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => emptyStagesFilter.Invoke(callContext));

        var nullDecisionStage = new NullDecisionStage();
        var silentStageFilter = new SiloAccessCallFilter([nullDecisionStage], ambient, new AccessOptions { UnstampedEdgeIsKernel = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => silentStageFilter.Invoke(callContext));
    }

    [Fact]
    public async Task GrainGrantPolicySource_OpenPostureSkip_OnlyAllowsProvenPerson_NeverAppAgentOrScheduler()
    {
        var auth = Options.Create(new AuthOptions { Posture = IdentityPosture.Open });
        var denyingIdentity = new RecordingDenyIdentityClient();
        var mirror = new AuthorityMirror();
        var source = new GrainGrantPolicySource(denyingIdentity, mirror, auth);

        // 1. Proven Owner person skips Open posture grant check and returns explicit Allow (never null).
        var provenPerson = CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin);
        var personDecision = await source.AuthorizeAsync(new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(provenPerson),
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = AccountSession.DefaultLogin,
        }, Ct);
        Assert.NotNull(personDecision);
        Assert.True(personDecision.Allowed);
        Assert.Equal(0, denyingIdentity.AuthorizeCalls);

        // 2. Agent, App, and Scheduler MUST NOT take the Open-posture skip; they evaluate Decide/Identity.
        var agent = CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin, CallerKind.Agent, CapabilityCallers.AssistantApp);
        var agentDecision = await source.AuthorizeAsync(new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(agent),
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = AccountSession.DefaultLogin,
        }, Ct);
        Assert.NotNull(agentDecision);
        Assert.False(agentDecision.Allowed);

        var scheduler = CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin, CallerKind.Scheduler, CapabilityCallers.SchedulerApp);
        var schedulerDecision = await source.AuthorizeAsync(new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(scheduler),
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = AccountSession.DefaultLogin,
        }, Ct);
        Assert.NotNull(schedulerDecision);
        Assert.False(schedulerDecision.Allowed);

        var arbitraryApp = CapabilityCallers.Of(AccountSession.DefaultLogin, AccountSession.DefaultLogin, AccountSession.DefaultLogin, CallerKind.App, "untrusted-app");
        var appDecision = await source.AuthorizeAsync(new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(arbitraryApp),
            TargetNeuron = "app.view/owner/main",
            Operation = "IView.Read",
            TargetScope = AccountSession.DefaultLogin,
        }, Ct);
        Assert.NotNull(appDecision);
        Assert.False(appDecision.Allowed);
    }

    [Fact]
    public async Task McpAndOpenRoutes_RequireValidPresenceToken_RejectUnprovedWith401()
    {
        var secret = RandomNumberGenerator.GetBytes(PresenceProofValidator.SecretByteCount);
        var token = System.Buffers.Text.Base64Url.EncodeToString(secret);
        var hash = PresenceProofValidator.ComputeTokenHash(secret);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(EdgePresenceSecurityFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [PresenceProofValidator.TokenHashConfigurationKey] = hash,
        });
        builder.Services.AddSingleton<IIdentityClient>(new AllowIdentityClient());
        builder.Services.AddIntoChatOSConnections();
        builder.Services.AddIdentity();
        builder.Services.AddIntoChatOSMcp();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAccountSession();
        IdentityEndpoints.Map(app);
        app.MapIntoChatOSMcp();
        app.MapGet("/probe/ok", () => Results.Ok(new { ok = true }));

        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // Unproved Open loopback calls on both regular routes and /mcp must return 401 Unauthorized (never "local").
        using var unprovedRoute = await client.GetAsync("/probe/ok", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unprovedRoute.StatusCode);

        using var unprovedMcpGet = await client.GetAsync("/mcp", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unprovedMcpGet.StatusCode);

        using var unprovedMcpPost = await client.SendAsync(McpInitializeRequest("/mcp"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unprovedMcpPost.StatusCode);

        using var unprovedBrainMcp = await client.SendAsync(McpInitializeRequest("/brains/owner/mcp"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unprovedBrainMcp.StatusCode);

        // Wrong presence token -> 401
        using var wrongMcp = McpInitializeRequest("/mcp");
        wrongMcp.Headers.Add(PresenceProofValidator.HeaderName, "wrong-token-000000000000000000000000000");
        using var wrongMcpRes = await client.SendAsync(wrongMcp, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongMcpRes.StatusCode);

        // Valid presence token -> 200 OK
        using var validRoute = new HttpRequestMessage(HttpMethod.Get, "/probe/ok");
        validRoute.Headers.Add(PresenceProofValidator.HeaderName, token);
        using var validRouteRes = await client.SendAsync(validRoute, Ct);
        Assert.Equal(HttpStatusCode.OK, validRouteRes.StatusCode);

        using var validMcp = McpInitializeRequest("/mcp");
        validMcp.Headers.Add(PresenceProofValidator.HeaderName, token);
        using var validMcpRes = await client.SendAsync(validMcp, Ct);
        Assert.Equal(HttpStatusCode.OK, validMcpRes.StatusCode);
    }

    private static HttpRequestMessage McpInitializeRequest(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return req;
    }

    private sealed class FixedCallerAmbient(Caller? current) : ICallerAmbient
    {
        public Caller? Current => current;
        public IDisposable Enter(Caller caller) => throw new NotSupportedException();
    }

    private sealed class NullDecisionStage : ICallFilterStage
    {
        public int Order => 0;
        public ValueTask<CallDecision?> EvaluateAsync(CallRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallDecision?>(null);
    }

    private sealed class FakeNeuronHost : INeuronHost
    {
        public Task<bool> IsInterleaved(RuntimeInvocation call) => Task.FromResult(false);
        public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task Post(RuntimeInvocation call) => Task.CompletedTask;
        public Task PostInterleaved(RuntimeInvocation call) => Task.CompletedTask;
        public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<JsonElement?> InspectState(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
    }

    private class InvokableProxy : DispatchProxy
    {
        public RuntimeInvocation Invocation { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "GetArgumentCount" => 1,
            "GetArgument" => Invocation,
            _ => targetMethod?.ReturnType is { IsValueType: true } vt && vt != typeof(void)
                ? Activator.CreateInstance(vt)
                : null,
        };
    }

    private sealed class FakeGrainCallContext : IIncomingGrainCallContext
    {
        private readonly FakeNeuronHost _grain = new();
        private readonly IInvokable _request;

        public FakeGrainCallContext(GrainId targetId, RuntimeInvocation invocation)
        {
            TargetId = targetId;
            var proxy = DispatchProxy.Create<IInvokable, InvokableProxy>();
            ((InvokableProxy)(object)proxy).Invocation = invocation;
            _request = proxy;
        }

        public GrainId TargetId { get; }
        public GrainId? SourceId => null;
        public string InterfaceName => "DigitalBrain.Runtime.Orleans.Hosting.INeuronHost";
        public string MethodName => "Invoke";
        public object Grain => _grain;
        public IInvokable Request => _request;
        public object? Result { get; set; }
        public Response? Response { get; set; } = Orleans.Serialization.Invocation.Response.FromResult<object?>(null);
        public MethodInfo InterfaceMethod => typeof(INeuronHost).GetMethod(nameof(INeuronHost.Invoke))!;
        public MethodInfo ImplementationMethod => InterfaceMethod;
        public IGrainContext TargetContext => null!;
        public GrainInterfaceType InterfaceType => default;
        public Task Invoke() => Task.CompletedTask;
    }

    private sealed class RecordingDenyIdentityClient : IIdentityClient
    {
        public int AuthorizeCalls { get; private set; }
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
        public Task<Member?> Membership(string brainScopeId, string principalId) => Task.FromResult<Member?>(null);
        public Task<Member?> EnsureMember(Member member) => Task.FromResult<Member?>(member);
        public Task RemoveMember(string brainScopeId, string accountId) => Task.CompletedTask;
        public Task RemovePrincipalMember(string brainScopeId, string principalId) => Task.CompletedTask;
        public Task<CallDecision?> Authorize(CallRequest request)
        {
            AuthorizeCalls++;
            return Task.FromResult<CallDecision?>(CallDecision.Deny(CallDenial.MissingGrant, "Denied by test identity client."));
        }
        public Task<Grant[]> ListGrants(string brainScopeId) => Task.FromResult<Grant[]>([]);
        public Task<Grant> GrantAsOwner(CapabilityCaller caller, Grant grant) => Task.FromResult(grant);
        public Task<Grant[]> GrantShipped(string brainScopeId, Grant[] grants) => Task.FromResult(grants);
        public Task RevokeAsOwner(CapabilityCaller caller, string grantId) => Task.CompletedTask;
        public Task RevokeAsOwner(CapabilityCaller caller, string appId, string semanticTypeId, GrantMode mode) => Task.CompletedTask;
        public Task<BrainProvision> RedeemExternalTicket(string issuer, string email, string? displayName = null) => throw new NotSupportedException();
    }

    private sealed class AllowIdentityClient : IIdentityClient
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
            BrainId = brainScopeId,
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
