using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing.Conformance;
using DigitalBrain.Testing.Conformance.Identity;
using DigitalBrain.Testing.Conformance.Secrets;
using IntoChat.Cloud.Auth.Services;
using IntoChat.Cloud.Organisations;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IntoChat.Cloud.Auth.Tests;

// The kernel composes the Auth module through the cloud module adapter in 09-09; here a test module puts it on an in-process os
// brain directly: its neurons run on the runtime's host grains, its services are the module's own, and its routes are the module
// HTTP surface mapped once onto the session-required root group.
public sealed class AuthHostModule() : IntoChat.OS.Framework.Modules.Module(new ModuleId(AuthModule.Id)), IHttpModule
{
    public override Type? Neurons => typeof(AuthModule);

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        new AuthModule().ConfigureServices(services);
    }

    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapModuleHttpSurfaces();
        // Stands in for the host's logout (09-09): it ends the session through the host's ISessionIssuer.
        endpoints.MapPost("/test/logout", async (HttpContext http, ISessionIssuer issuer) =>
        {
            await issuer.EndAsync(http);
            return Results.NoContent();
        }).AllowAnonymous();
    }
}

// What the cloud host binds in 09-09, stood in for the tests: INeuronProxies over the kernel's plain neuron access.
internal sealed class PlainNeuronProxies(IPlainNeurons plain) : INeuronProxies
{
    public T Get<T>(NeuronId id) where T : class => plain.Get<T>(id);
}

// The host's ISessionIssuer: identity@1 ISessions.Issue (through the runtime capability, so the privileged grant is checked),
// then the session cookie of the host's cookie scheme carrying the session principal.
internal sealed class TestSessionIssuer(ISessions sessions, IEnumerable<ISignInHandler> handlers, IAuthHostSeam host) : ISessionIssuer
{
    public async Task<SessionIssued> IssueAsync(HttpContext http, SessionRequest request, CancellationToken cancellationToken = default)
    {
        var issued = await sessions.Issue(request, cancellationToken);
        var principal = SessionPrincipal.Create(request, issued, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { ExpiresUtc = issued.ExpiresAt, IsPersistent = true });
        // The host's sign-in hook: handlers run after the session is issued and never fail sign-in.
        foreach (var handler in handlers)
        {
            try { await handler.SignedIn(request.Member, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { _ = error; }
        }

        return issued;
    }

    public async Task EndAsync(HttpContext http, CancellationToken cancellationToken = default)
    {
        // The host is trusted to end the session it issued; the runtime still checks the module grant, so it ends it as Auth.
        if (SessionPrincipal.SessionId(http.User) is { } sid)
        {
            using (AuthSecrets.Stamp(host)) { await sessions.End(sid, cancellationToken); }
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}

// What the cloud host binds in 09-09 over its runtime: the runtime-stamped caller, the module caller stamp for capability calls, and the
// module-call scope an HTTP-edge request needs to reach the module's [Internal] neurons.
internal sealed class TestAuthHostSeam : IAuthHostSeam
{
    public CapabilityCaller? CurrentCaller => RuntimeCallerContext.Current;

    public IDisposable StampModuleCaller(CapabilityCaller module) => RuntimeCallerContext.Stamp(module);

    public async Task<T> InModuleCall<T>(Func<Task<T>> call)
    {
        var previous = global::Orleans.Runtime.RequestContext.Get(IntoChat.OS.Framework.CallerContextStamper.ModuleCallKey);
        global::Orleans.Runtime.RequestContext.Set(IntoChat.OS.Framework.CallerContextStamper.ModuleCallKey, true);
        try { return await call(); }
        finally
        {
            if (previous is null) { global::Orleans.Runtime.RequestContext.Remove(IntoChat.OS.Framework.CallerContextStamper.ModuleCallKey); }
            else { global::Orleans.Runtime.RequestContext.Set(IntoChat.OS.Framework.CallerContextStamper.ModuleCallKey, previous); }
        }
    }
}

// Stamps the calling module on the runtime capabilities the way a host edge does.
internal sealed class RuntimeCallerScope : CallerScope
{
    public override IDisposable As(KitCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return RuntimeCallerContext.Stamp(new CapabilityCaller(caller.PrincipalId, caller.AccountId, caller.BrainId, caller.AppId, caller.ConversationId));
    }
}

// The capabilities the Auth module calls, over the in-memory kit ports: identity@1 provisioning and secrets@1 with the deployment
// secrets the manifest declares. Sessions are Auth's own port behind the runtime SessionsCapability, so the grant is enforced.
internal static class AuthCapabilities
{
    public static ModuleDeclarations Declarations { get; } = new([ModuleManifest.Of(typeof(AuthModule))]);

    // identity@1 provisioning and secrets@1 are the kernel's (the Authority directory and vault, whose Named reads the module's
    // Secrets configuration section). Sessions are Auth's own port behind the runtime SessionsCapability.
    public static void Register(IServiceCollection services, IReadOnlyDictionary<string, string> deploymentSecrets)
    {
        services.AddSingleton<IHostedService>(provider => new SeedDeploymentSecrets(provider.GetRequiredService<IConfiguration>(), deploymentSecrets));
        services.AddKeyedSingleton<ISessions>(Capability.Identity.V1Id, (provider, _) => new SessionsCapability(provider.GetRequiredService<AuthIdentityProvider>(), Declarations));
        services.AddSingleton<IAuthHostSeam, TestAuthHostSeam>();
        services.AddSingleton<INeuronProxies>(provider => new PlainNeuronProxies(provider.GetRequiredService<IPlainNeurons>()));
        services.AddSingleton<ISessionIssuer>(provider => new TestSessionIssuer(provider.GetRequiredKeyedService<ISessions>(Capability.Identity.V1Id), provider.GetServices<ISignInHandler>(), provider.GetRequiredService<IAuthHostSeam>()));
    }
}

// The deployment's declared secrets as the installer carries them (IntoChatOS:Modules:auth:Secrets:{name}); added to the host's
// configuration when it starts, because a test cannot pass host-owned module keys through TestBrain.WithConfiguration.
internal sealed class SeedDeploymentSecrets(IConfiguration configuration, IReadOnlyDictionary<string, string> secrets) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ((IConfigurationManager)configuration).AddInMemoryCollection(secrets.ToDictionary(
            pair => AuthModule.OptionsSection[..AuthModule.OptionsSection.LastIndexOf(":Options", StringComparison.Ordinal)] + ":Secrets:" + pair.Key,
            pair => (string?)pair.Value));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Stand-ins for the Organisations module (09-06): the directory makes a personal organisation and records the membership, the
// membership neuron answers AuthAudit, and the organisation neuron answers the audit log's Admin check.
[DigitalBrain.Neuron(IOrgMembership.Type)]
internal sealed class OrgMembershipStandIn : DigitalBrain.Neuron, IOrgMembership
{
    private OrgMembershipState? state;

    protected override async Task OnActivate(CancellationToken cancellationToken) => state = await Context.State.Load<OrgMembershipState>();

    public Task<OrgMembershipState?> Get() => Task.FromResult(state);

    public async Task Set(string orgId, string orgBrain, string emailHash, string personalOrgBrain, string? expectedOrgId = null)
    {
        var next = new OrgMembershipState { OrgId = orgId, OrgBrain = orgBrain, EmailHash = emailHash, PersonalOrgBrain = personalOrgBrain };
        await Context.State.Save(next, []);
        state = next;
    }
}

[DigitalBrain.Neuron(IOrganisationDirectory.Type)]
internal sealed class OrganisationDirectoryStandIn : DigitalBrain.Neuron, IOrganisationDirectory
{
    private PersonalOrganisationResult? created;

    protected override async Task OnActivate(CancellationToken cancellationToken) => created = await Context.State.Load<PersonalOrganisationResult>();

    public async Task<PersonalOrganisationResult> EnsurePersonal(PersonalOrganisationRequest request)
    {
        if (created is not null) { return created with { Created = false }; }
        var orgId = "org-" + request.PrincipalId;
        var proxies = Context.Services.GetRequiredService<INeuronProxies>();
        await proxies.Get<IOrgMembership>(AuthIds.Of(IOrgMembership.Type, IOrgMembership.Key(request.PrincipalId)))
            .Set(orgId, request.BrainId, "", request.BrainId);
        await proxies.Get<IOrganisation>(AuthIds.Of(IOrganisation.Type, IOrganisation.Key(request.BrainId))).CreatePersonal(request);
        created = new PersonalOrganisationResult(orgId, OrganisationRole.Admin, true);
        await Context.State.Save(created, []);
        return created;
    }
}

[DigitalBrain.Neuron(IOrganisation.Type)]
internal sealed class OrganisationStandIn : DigitalBrain.Neuron, IOrganisation
{
    private OrganisationState? state;

    protected override async Task OnActivate(CancellationToken cancellationToken) => state = await Context.State.Load<OrganisationState>();

    public async Task<bool> CreatePersonal(PersonalOrganisationRequest request)
    {
        if (state is not null) { return false; }
        state = new OrganisationState
        {
            OrgId = "org-" + request.PrincipalId,
            OrgBrain = request.BrainId,
            Name = request.DisplayName,
            PersonalOwnerPrincipalId = request.PrincipalId,
            Members = [new OrganisationMember { PrincipalId = request.PrincipalId, DisplayName = request.DisplayName, Email = request.Email, Role = OrganisationRole.Admin, JoinedAt = request.VerifiedAt }],
        };
        await Context.State.Save(state, []);
        return true;
    }

    public Task<OrganisationRole?> RoleOf(string principalId) => Task.FromResult(state?.Members.FirstOrDefault(member => member.PrincipalId == principalId)?.Role);

    public Task<OrganisationState> Read() => Task.FromResult(state ?? new OrganisationState());

    public Task<OrganisationState> Get() => Read();

    public Task Rename(string name) => throw new NotSupportedException();

    public Task ChangeRole(string principalId, OrganisationRole role) => throw new NotSupportedException();

    public Task RemoveMember(string principalId) => throw new NotSupportedException();

    public Task AddMember(OrganisationMember member) => throw new NotSupportedException();

    public Task Dissolve() => throw new NotSupportedException();

    public Task Revive(string name, OrganisationMember admin) => throw new NotSupportedException();

    public Task RemoveMemberRecord(string principalId) => throw new NotSupportedException();
}

// The stand-ins above are neurons of the Organisations module's contracts; this module brings them to the runtime.
[DigitalBrain.Module("organisations")]
public sealed class OrganisationStandIns : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class OrganisationsHostModule() : IntoChat.OS.Framework.Modules.Module(new ModuleId("organisations"))
{
    public override Type? Neurons => typeof(OrganisationStandIns);
}
