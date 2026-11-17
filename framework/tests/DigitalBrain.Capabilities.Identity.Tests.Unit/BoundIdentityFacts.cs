namespace DigitalBrain.Capabilities.Identity.Tests.Unit;

using DigitalBrain.Access;
using DigitalBrain.Testing;
using Xunit;

public sealed class BoundIdentityFacts : IAsyncLifetime
{
    private TestOS _os = null!;

    public ValueTask InitializeAsync()
    {
        _os = TestOS.Local();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _os.DisposeAsync();

    [Fact]
    public void NoPortMemberTakesACaller()
    {
        var types = typeof(IIdentityDirectoryPort).GetMethods().Concat(typeof(IIdentitySessionPort).GetMethods())
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(types, type => type.Name.Contains("Caller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGrantWithNoStampedCallerIsRefused()
    {
        var identity = new BoundIdentity(new RecordingPort(), new Ambient(null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.GrantAsOwner(new Grant { AppId = "a", WorkspaceId = "b", SemanticTypeId = "t" }, TestContext.Current.CancellationToken));
        var decision = await identity.Authorize(new CallRequest { TargetNeuron = "n", Operation = "o", SemanticTypeIds = ["t"] }, TestContext.Current.CancellationToken);
        Assert.Equal(CallDenial.NotAuthenticated, decision!.Denial);
    }

    [Fact]
    public async Task AGrantTheOwnerPortAcceptsIsReturned()
    {
        var port = new RecordingPort { GrantResult = new Grant { AppId = "a", WorkspaceId = "b", SemanticTypeId = "t" } };
        var identity = new BoundIdentity(port, new Ambient(_os.Edge.Person("alice", "acc", new BrainId("b"))));
        var grant = await identity.GrantAsOwner(port.GrantResult, TestContext.Current.CancellationToken);
        Assert.Equal(port.GrantResult, grant);
    }

    [Fact]
    public async Task AGrantThePortRefusesIsUnauthorizedAndNamesThePerson()
    {
        var identity = new BoundIdentity(new RecordingPort(), new Ambient(_os.Edge.Person("alice", "acc", new BrainId("b"))));
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.GrantAsOwner(new Grant { AppId = "a", WorkspaceId = "b", SemanticTypeId = "t" }, TestContext.Current.CancellationToken));
        Assert.Contains("alice", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.RevokeAsOwner("a", "t", GrantMode.Always, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SessionsReadThePrivilegedAppKeyFromTheStampedApp()
    {
        string? seen = "unset";
        var alice = new Actor.Person("alice", "acc");
        var app = new BoundSessions(new SessionPort(), new Ambient(_os.Edge.App("auth", alice, new BrainId("b"))), (appId, _, _) => seen = appId);
        await app.End("s1", TestContext.Current.CancellationToken);
        Assert.Equal("auth", seen);

        var person = new BoundSessions(new SessionPort(), new Ambient(_os.Edge.Person("alice", "acc", new BrainId("b"))), (appId, _, _) => seen = appId);
        await person.End("s1", TestContext.Current.CancellationToken);
        Assert.Null(seen);

        var nobody = new BoundSessions(new SessionPort(), new Ambient(null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => nobody.End("s1", TestContext.Current.CancellationToken));
    }

    private sealed class Ambient(Caller? caller) : ICallerAmbient
    {
        public Caller? Current => caller;
    }

    private sealed class SessionPort : IIdentitySessionPort
    {
        public Task<SessionIssued> Issue(SessionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task End(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<Member?> Validate(string sessionId, CancellationToken cancellationToken) => Task.FromResult<Member?>(null);

        public IdentityProviderDescriptor Describe() => throw new NotSupportedException();
    }

    private sealed class RecordingPort : IIdentityDirectoryPort
    {
        public Grant? GrantResult { get; init; }

        public Task<Grant?> GrantAsOwner(Grant grant, CancellationToken cancellationToken) => Task.FromResult(GrantResult);

        public Task<bool> RevokeAsOwner(string appId, string semanticTypeId, GrantMode mode, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<CallDecision?> Authorize(CallRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task EnsureAccount(Account account, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Grant>> ListGrants(string brainScopeId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
