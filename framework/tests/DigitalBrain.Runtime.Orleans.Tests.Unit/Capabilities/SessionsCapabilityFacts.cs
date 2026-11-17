using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Modules;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Capabilities;

// 09-03 (T-09-08): identity@1 sessions are fail closed in the runtime.
public sealed class SessionsCapabilityFacts
{
    [Fact]
    public async Task Unstamped_call_is_denied()
    {
        var sessions = new BoundSessions(new InMemoryIdentityDirectory(callers: OrleansCallerAmbient.Instance), OrleansCallerAmbient.Instance, KitDeclarations.Identity.RequirePrivileged);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => sessions.Validate("s", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Host_without_composed_modules_refuses_every_session_call_but_describes()
    {
        var services = new ServiceCollection().AddIdentitySessions<InMemoryIdentityDirectory>().BuildServiceProvider();
        var sessions = services.GetRequiredKeyedService<ISessions>(Capability.Identity.V1Id);
        Assert.Same(sessions, services.GetRequiredService<ISessions>());
        Assert.Same(sessions, services.GetRequiredService<IIdentityProvider>());
        Assert.Same(sessions, services.GetRequiredKeyedService<IIdentityProvider>(Capability.Identity.V1Id));
        Assert.Equal(IdentityPosture.Secured, services.GetRequiredKeyedService<IIdentityProvider>(Capability.Identity.V1Id).Describe().Posture);
        using (Stamps.Enter(Stamps.App(KitDeclarations.PrivilegedModule, "app:kit-auth", "a", "b")))
        {
            var denied = await Assert.ThrowsAsync<PrivilegeDenied>(() => sessions.Validate("s", TestContext.Current.CancellationToken));
            Assert.Equal(KitDeclarations.PrivilegedModule, denied.ModuleId);
        }
    }

    [Fact]
    public async Task Identity_owned_claims_never_reach_the_port()
    {
        var sessions = new BoundSessions(new ThrowingPort(), OrleansCallerAmbient.Instance, KitDeclarations.Identity.RequirePrivileged);
        var member = new Member { PrincipalId = "p", AccountId = "a", BrainId = "b", Role = MemberRole.Owner, DisplayName = "P" };
        using (Stamps.Enter(Stamps.App(KitDeclarations.PrivilegedModule, "app:kit-auth", "a", "b")))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => sessions.Issue(
                new SessionRequest { Member = member, Claims = new Dictionary<string, string> { ["brain"] = "other" } }, TestContext.Current.CancellationToken));
        }
    }

    private sealed class ThrowingPort : IIdentitySessionPort
    {
        public Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("reached the port");

        public Task End(string sessionId, CancellationToken cancellationToken) => throw new InvalidOperationException("reached the port");

        public Task<Member?> Validate(string sessionId, CancellationToken cancellationToken) => throw new InvalidOperationException("reached the port");

        public IdentityProviderDescriptor Describe() => new() { ProviderId = "throwing" };
    }
}
