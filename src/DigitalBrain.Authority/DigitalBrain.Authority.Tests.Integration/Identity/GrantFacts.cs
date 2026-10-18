using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Authority.Identity;
using DigitalBrain.Authority.Identity.Authority;
using DigitalBrain.Authority.Identity.Grants;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Authority.Tests.Integration.Identity;

public sealed class GrantFacts(AuthorityHost host)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnAppCallerNeedsAGrantForEverySemanticType()
    {
        var request = Request(CallerKind.App, "app-1", "ws-1", "person.birthDate");
        var denied = GrantRules.Evaluate(request, null, []);
        Assert.Equal(CallDenial.MissingGrant, denied!.Denial);

        var allowed = GrantRules.Evaluate(request, null, [Grant(GrantMode.Always, "person.birthDate", "app-1", "ws-1")]);
        Assert.True(allowed!.Allowed);
    }

    [Fact]
    public void ARevokedOrForeignGrantLooksMissing()
    {
        var request = Request(CallerKind.App, "app-1", "ws-1", "person.birthDate");
        Assert.Equal(CallDenial.MissingGrant,
            GrantRules.Evaluate(request, null, [Grant(GrantMode.Always, "person.birthDate", "app-1", "ws-1") with { Revoked = true }])!.Denial);
        Assert.Equal(CallDenial.MissingGrant,
            GrantRules.Evaluate(request, null, [Grant(GrantMode.Always, "person.birthDate", "app-2", "ws-1")])!.Denial);
        Assert.Equal(CallDenial.MissingGrant,
            GrantRules.Evaluate(request, null, [Grant(GrantMode.Always, "person.birthDate", "app-1", "ws-2")])!.Denial);
    }

    [Fact]
    public void AThisChatGrantNeedsTheMatchingConversation()
    {
        var request = Request(CallerKind.App, "app-1", "ws-1", "person.birthDate") with
        {
            Caller = Request(CallerKind.App, "app-1", "ws-1", "person.birthDate").Caller with { ConversationId = "chat-1" },
        };
        var matching = Grant(GrantMode.ThisChat, "person.birthDate", "app-1", "ws-1") with { ConversationId = "chat-1" };
        var other = Grant(GrantMode.ThisChat, "person.birthDate", "app-1", "ws-1") with { ConversationId = "chat-2" };
        Assert.True(GrantRules.Evaluate(request, null, [matching])!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, GrantRules.Evaluate(request, null, [other])!.Denial);
    }

    [Fact]
    public void UserAccessInsideItsOwnWorkspaceNeedsNoGrant()
    {
        var request = Request(CallerKind.User, null, "ws-1", "person.birthDate");
        var member = new Member { PrincipalId = "principal-1", AccountId = "account-1", BrainId = "ws-1", Role = MemberRole.Owner, DisplayName = "Owner", JoinedAt = Now };
        Assert.Null(GrantRules.Evaluate(request, member, []));
    }

    [Fact]
    public void AMemberOfAnotherWorkspaceIsDenied()
    {
        var request = Request(CallerKind.User, null, "ws-1", "person.birthDate");
        var member = new Member { PrincipalId = "principal-1", AccountId = "account-1", BrainId = "ws-2", Role = MemberRole.Owner, DisplayName = "Owner", JoinedAt = Now };
        Assert.Equal(CallDenial.OutsideWorkspace, GrantRules.Evaluate(request, member, [])!.Denial);
    }

    [Fact]
    public async Task TheStageFetchesMembershipAndGrantsThenAppliesTheRules()
    {
        var stage = new GrantCallFilterStage(new FakePolicy(
            new Member { PrincipalId = "principal-1", AccountId = "account-1", BrainId = "ws-1", Role = MemberRole.Owner, DisplayName = "Owner", JoinedAt = Now },
            [Grant(GrantMode.Always, "person.birthDate", "app-1", "ws-2")]));
        var request = Request(CallerKind.App, "app-1", "ws-1", "person.birthDate");
        var decision = await stage.EvaluateAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(CallDenial.MissingGrant, decision!.Denial);
    }

    [Fact]
    public async Task AOnceGrantIsConsumedAfterOneSuccessfulRead()
    {
        var policy = new RecordingPolicy([Grant(GrantMode.Once, "person.birthDate", "app-1", "ws-1")]);
        var stage = new GrantCallFilterStage(policy);
        var decision = await stage.EvaluateAsync(Request(CallerKind.App, "app-1", "ws-1", "person.birthDate"), TestContext.Current.CancellationToken);

        Assert.True(decision!.Allowed);
        var spent = Assert.Single(policy.Consumed);
        Assert.Equal("person.birthDate", spent.SemanticTypeId);
        Assert.Equal(GrantMode.Once, spent.Mode);
    }

    [Fact]
    public async Task AnAlwaysGrantIsNotConsumed()
    {
        var policy = new RecordingPolicy([Grant(GrantMode.Always, "person.birthDate", "app-1", "ws-1")]);
        var stage = new GrantCallFilterStage(policy);
        var decision = await stage.EvaluateAsync(Request(CallerKind.App, "app-1", "ws-1", "person.birthDate"), TestContext.Current.CancellationToken);

        Assert.True(decision!.Allowed);
        Assert.Empty(policy.Consumed);
    }

    [Fact]
    public async Task ADeniedReadConsumesNoGrant()
    {
        var policy = new RecordingPolicy([Grant(GrantMode.Once, "person.birthDate", "app-1", "ws-2")]);
        var stage = new GrantCallFilterStage(policy);
        var decision = await stage.EvaluateAsync(Request(CallerKind.App, "app-1", "ws-1", "person.birthDate"), TestContext.Current.CancellationToken);

        Assert.Equal(CallDenial.MissingGrant, decision!.Denial);
        Assert.Empty(policy.Consumed);
    }

    [Fact]
    public async Task RevokingAOnceGrantEmptiesTheAuthorityAndTheNextReadLooksMissing()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(account1Id, ws1Id).Id);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Once, "person.birthDate", "app-1", ws1Id));
        Assert.Single(await authority.ListGrants());

        await authority.Revoke("app-1", "person.birthDate", GrantMode.Once);
        Assert.Empty(await authority.ListGrants());

        var decision = GrantRules.Evaluate(
            RequestIn(account1Id, CallerKind.App, "app-1", ws1Id, "person.birthDate"), null, await authority.ListGrants());
        Assert.Equal(CallDenial.MissingGrant, decision!.Denial);
        Assert.Contains("missing", decision.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentCallsCannotSpendTheSameOnceGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(account1Id, ws1Id).Id);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Once, "person.birthDate", "app-1", ws1Id));
        var request = RequestIn(account1Id, CallerKind.App, "app-1", ws1Id, "person.birthDate");
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => authority.Consume(request)));
        Assert.Single(results, result => result is { Allowed: true });
        Assert.Empty(await authority.ListGrants());
    }

    // R68: the decision is a read; only a consume spends a Once grant.
    [Fact]
    public async Task AnAuthorizeReadNeverConsumesAGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(account1Id, ws1Id).Id);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Once, "person.birthDate", "app-1", ws1Id));
        var request = RequestIn(account1Id, CallerKind.App, "app-1", ws1Id, "person.birthDate");

        foreach (var _ in Enumerable.Range(0, 3)) { Assert.True((await authority.Authorize(request)) is { Allowed: true, ConsumesOnce: true }); }
        Assert.Single(await authority.ListGrants());
    }

    [Fact]
    public async Task AMirroredAuthorityAnswersTheNextCallWithoutTheGrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var mirror = new AuthorityMirror();
        var identity = new CountingAuthority();
        var source = new GrainGrantPolicySource(identity, mirror);
        var request = Request(CallerKind.App, "app-1", "ws-1");
        Assert.Null(await source.AuthorizeAsync(request, ct));
        Assert.Equal(1, identity.Authorizes);

        var scope = BrainScope.Create("account-1", "ws-1").Id;
        mirror.Publish(scope, new BrainAuthorityState { AccountId = "account-1", BrainId = "ws-1" });
        Assert.Null(await source.AuthorizeAsync(request, ct));
        Assert.Equal(1, identity.Authorizes);

        mirror.Publish(scope, new BrainAuthorityState { AccountId = "other", BrainId = "ws-1" });
        var denied = await source.AuthorizeAsync(request, ct);
        Assert.Equal(CallDenial.OutsideWorkspace, denied!.Denial);
        Assert.Equal(1, identity.Authorizes);

        mirror.Publish(scope, new BrainAuthorityState
        {
            AccountId = "account-1",
            BrainId = "ws-1",
            Grants = [Grant(GrantMode.Once, "person.birthDate", "app-1", "ws-1")],
        });
        Assert.Null(await source.AuthorizeAsync(Request(CallerKind.App, "app-1", "ws-1", "person.birthDate"), ct));
        Assert.Equal(2, identity.Authorizes);
    }

    [Fact]
    public async Task RevokingAGrantIsVisibleToTheSameProcessMirrorWhenTheWriteReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var scope = BrainScope.Create(account1Id, ws1Id).Id;
        var authority = brain.Platform.BrainAuthority(scope);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Always, "person.birthDate", "app-1", ws1Id));
        var mirror = brain.Services.GetRequiredService<AuthorityMirror>();
        var request = RequestIn(account1Id, CallerKind.App, "app-1", ws1Id, "person.birthDate");

        Assert.True(mirror.TryDecide(request, out var allowed));
        Assert.True(allowed!.Allowed);
        await authority.Revoke("app-1", "person.birthDate", GrantMode.Always);
        Assert.True(mirror.TryDecide(request, out var denied));
        Assert.Equal(CallDenial.MissingGrant, denied!.Denial);

        await authority.Grant(Grant(GrantMode.Once, "person.birthDate", "app-1", ws1Id));
        Assert.False(mirror.TryDecide(request, out _));
    }

    [Fact]
    public async Task TheAuthorityGrantsRevokesAndRelists()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(account1Id, ws1Id).Id);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Always, "person.birthDate", "app-1", ws1Id));
        Assert.Single(await authority.ListGrants());
        await authority.Revoke("app-1", "person.birthDate", GrantMode.Always);
        Assert.Empty(await authority.ListGrants());
    }

    [Fact]
    public async Task RevokingOneModeKeepsTheOthersAndARevokedValueLooksEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var ws1Id = AuthorityHost.Unique("ws-1");
        var account1Id = AuthorityHost.Unique("account-1");
        var authority = brain.Platform.BrainAuthority(BrainScope.Create(account1Id, ws1Id).Id);
        await authority.Initialize(account1Id, ws1Id);
        await authority.Grant(Grant(GrantMode.Always, "person.birthDate", "app-1", ws1Id));
        await authority.Grant(Grant(GrantMode.ThisChat, "person.birthDate", "app-1", ws1Id) with { ConversationId = "chat-1" });
        Assert.Equal(2, (await authority.ListGrants()).Length);

        await authority.Revoke("app-1", "person.birthDate", GrantMode.Always);
        var remaining = Assert.Single(await authority.ListGrants());
        Assert.Equal(GrantMode.ThisChat, remaining.Mode);

        var request = RequestIn(account1Id, CallerKind.App, "app-1", ws1Id, "person.birthDate");
        var revoked = Grant(GrantMode.Always, "person.birthDate", "app-1", ws1Id) with { Revoked = true };
        var decision = GrantRules.Evaluate(request, null, [revoked]);
        Assert.Equal(CallDenial.MissingGrant, decision!.Denial);
        Assert.Contains("missing", decision.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    private static Grant Grant(GrantMode mode, string semanticTypeId, string appId, string workspaceId) => new()
    {
        AppId = appId,
        SemanticTypeId = semanticTypeId,
        Mode = mode,
        WorkspaceId = workspaceId,
        GrantedAt = Now,
    };

    private static CallRequest Request(CallerKind kind, string? appId, string workspaceId, params string[] semanticTypes)
        => RequestIn("account-1", kind, appId, workspaceId, semanticTypes);

    private static CallRequest RequestIn(string accountId, CallerKind kind, string? appId, string workspaceId, params string[] semanticTypes) => new()
    {
        Caller = new CallerContext
        {
            PrincipalId = "principal-1",
            AccountId = accountId,
            BrainId = workspaceId,
            Kind = kind,
            StampedBy = kind == CallerKind.App ? TrustedEdge.AppProxy : TrustedEdge.AuthenticatedHttp,
            AppId = appId,
        },
        TargetNeuron = "neuron-1",
        Operation = "Read",
        SemanticTypeIds = semanticTypes,
    };

    private sealed class CountingAuthority : IIdentityClient
    {
        public int Authorizes { get; private set; }
        public Task<CallDecision?> Authorize(CallRequest request)
        {
            Authorizes++;
            return Task.FromResult<CallDecision?>(null);
        }
        public Task<Account?> ReadAccount(string accountId) => throw new NotSupportedException();
        public Task<Account?> OwnerOfBrain(string brainId) => throw new NotSupportedException();
        public Task EnsureAccount(Account account) => throw new NotSupportedException();
        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind) => throw new NotSupportedException();
        public Task<BrainProvision> EnsureBrain(Member owner) => throw new NotSupportedException();
        public Task<BrainProvision> ProvisionOwner(Member owner) => throw new NotSupportedException();
        public Task<bool> IsRegistered(string principalId) => throw new NotSupportedException();
        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName) => throw new NotSupportedException();
        public Task<BrainProvision?> CompleteRegistration(string principalId) => throw new NotSupportedException();
        public Task<BrainEntry[]> BrainsOf(string principalId) => throw new NotSupportedException();
        public Task<BrainKind> KindOf(string brainScopeId) => throw new NotSupportedException();
        public Task<bool> BrainExists(string brainScopeId) => throw new NotSupportedException();
        public Task<Member?> Membership(string brainScopeId, string principalId) => throw new NotSupportedException();
        public Task<Member?> EnsureMember(Member member) => throw new NotSupportedException();
        public Task RemoveMember(string brainScopeId, string accountId) => throw new NotSupportedException();
        public Task<Grant[]> ListGrants(string brainScopeId) => throw new NotSupportedException();
        public Task<Grant> GrantAsOwner(CallerContext caller, Grant grant) => throw new NotSupportedException();
        public Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode) => throw new NotSupportedException();
    }

    private sealed class FakePolicy(Member? member, IReadOnlyList<Grant> grants) : IGrantPolicySource
    {
        public ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(GrantRules.Evaluate(request, member, grants));

        public ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CallerContext caller, CancellationToken cancellationToken)
            => ValueTask.FromResult(grants);

    }

    private sealed class RecordingPolicy(IReadOnlyList<Grant> grants) : IGrantPolicySource
    {
        public List<Grant> Consumed { get; } = [];

        public ValueTask<CallDecision?> AuthorizeAsync(CallRequest request, CancellationToken cancellationToken)
        {
            var decision = GrantRules.Evaluate(request, null, grants);
            if (decision is { Allowed: true }) { Consumed.AddRange(GrantRules.OnceToConsume(request, grants)); }
            return ValueTask.FromResult(decision);
        }

        public ValueTask<IReadOnlyList<Grant>> ListGrantsAsync(CallerContext caller, CancellationToken cancellationToken)
            => ValueTask.FromResult(grants);

    }
}
