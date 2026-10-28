using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

public sealed class ProvisioningRaceFacts(AuthHost host)
{
    [Fact]
    public async Task Concurrent_initial_reads_resume_the_same_identity_registration()
    {
        var services = host.Brain.Services;
        var identity = services.GetRequiredKeyedService<IIdentity>(Capability.Identity.V1Id);
        var gated = new InitialReadBarrier(identity);
        var provisioner = Provisioner(gated);
        var principal = "race-" + Guid.NewGuid().ToString("N");
        var members = await Task.WhenAll(provisioner.RegisterAsync(principal, "Person"), provisioner.RegisterAsync(principal, "Person"));
        Assert.Equal(members[0].AccountId, members[1].AccountId);
        Assert.Equal(members[0].BrainId, members[1].BrainId);
        Assert.Single(await identity.BrainsOf(principal, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Registration_failure_without_existing_membership_remains_an_error()
    {
        var identity = host.Brain.Services.GetRequiredKeyedService<IIdentity>(Capability.Identity.V1Id);
        var rejected = new InitialReadBarrier(identity, reject: true);
        var provisioner = Provisioner(rejected);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.RegisterAsync("rejected-" + Guid.NewGuid().ToString("N"), "Person"));
        Assert.Same(rejected.Failure, error);
    }

    // The provisioner over a service provider whose identity@1 is the gated one.
    private AccountProvisioner Provisioner(IIdentity identity)
    {
        var services = host.Brain.Services;
        var gated = new ServiceCollection().AddKeyedSingleton(Capability.Identity.V1Id, identity).BuildServiceProvider();
        return new AccountProvisioner(gated, services.GetRequiredService<AuthCalls>(), host.Clock, host.Hashing, services.GetRequiredService<CodeMailer>());
    }

    // Delegates to the real identity, holding only the first two reads so both callers observe no registration.
    private sealed class InitialReadBarrier(IIdentity inner, bool reject = false) : IIdentity
    {
        private readonly TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;

        public InvalidOperationException Failure { get; } = new("Synthetic registration failure");

        public async Task<BrainProvision?> CompleteRegistration(string principalId, CancellationToken cancellationToken = default)
        {
            var value = await inner.CompleteRegistration(principalId, cancellationToken);
            if (!reject && Interlocked.Increment(ref reads) <= 2)
            {
                if (Volatile.Read(ref reads) == 2) { barrier.TrySetResult(); }
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }

            return value;
        }

        public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName, CancellationToken cancellationToken = default) => reject
            ? Task.FromException<BrainProvision>(Failure) : inner.RegisterPrincipal(principalId, displayName, cancellationToken);

        public Task<Account?> ReadAccount(string accountId, CancellationToken cancellationToken = default) => inner.ReadAccount(accountId, cancellationToken);

        public Task<Account?> OwnerOfBrain(string brainId, CancellationToken cancellationToken = default) => inner.OwnerOfBrain(brainId, cancellationToken);

        public Task EnsureAccount(Account account, CancellationToken cancellationToken = default) => inner.EnsureAccount(account, cancellationToken);

        public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind, CancellationToken cancellationToken = default)
            => inner.CreateBrain(accountId, principalId, operationId, displayName, kind, cancellationToken);

        public Task<BrainProvision> EnsureBrain(Member owner, CancellationToken cancellationToken = default) => inner.EnsureBrain(owner, cancellationToken);

        public Task<BrainProvision> ProvisionOwner(Member owner, CancellationToken cancellationToken = default) => inner.ProvisionOwner(owner, cancellationToken);

        public Task<bool> IsRegistered(string principalId, CancellationToken cancellationToken = default) => inner.IsRegistered(principalId, cancellationToken);

        public Task<IReadOnlyList<BrainEntry>> BrainsOf(string principalId, CancellationToken cancellationToken = default) => inner.BrainsOf(principalId, cancellationToken);

        public Task<BrainKind> KindOf(string brainScopeId, CancellationToken cancellationToken = default) => inner.KindOf(brainScopeId, cancellationToken);

        public Task<bool> BrainExists(string brainScopeId, CancellationToken cancellationToken = default) => inner.BrainExists(brainScopeId, cancellationToken);

        public Task<Member?> Membership(string brainScopeId, string principalId, CancellationToken cancellationToken = default) => inner.Membership(brainScopeId, principalId, cancellationToken);

        public Task<Member?> EnsureMember(Member member, CancellationToken cancellationToken = default) => inner.EnsureMember(member, cancellationToken);

        public Task RemoveMember(string brainScopeId, string accountId, CancellationToken cancellationToken = default) => inner.RemoveMember(brainScopeId, accountId, cancellationToken);

        public Task RemovePrincipalMember(string brainScopeId, string principalId, CancellationToken cancellationToken = default) => inner.RemovePrincipalMember(brainScopeId, principalId, cancellationToken);
    }
}
