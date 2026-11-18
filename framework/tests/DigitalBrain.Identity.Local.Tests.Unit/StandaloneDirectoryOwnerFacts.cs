using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Testing;

namespace DigitalBrain.Identity.Local.Tests.Unit;

// The standalone directory decides an owner grant from the caller the edge stamped, never from a caller the request names.
public sealed class StandaloneDirectoryOwnerFacts
{
    [Fact]
    public async Task ForgedOwnerCannotGrantInTheStandaloneDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local();
        os.Register<DirectoryNeuron>();
        var port = new KernelHostPort(new LocalDirectoryPort((contract, key) => LocalIdentityConformance.ResolveNeuron(os, contract, key), null, os.Callers, os.Edge), os.Edge);
        var identity = new BoundIdentity(port, os.Callers);
        var owner = (await identity.RegisterPrincipal("alice", "Alice", ct)).Member;
        var stranger = (await identity.RegisterPrincipal("mallory", "Mallory", ct)).Member;
        var brain = new BrainId(owner.BrainId);
        var grant = new Grant { AppId = "notes", SemanticTypeId = "kit.notes", Mode = GrantMode.Always, WorkspaceId = owner.BrainId };
        var alice = new Actor.Person(owner.PrincipalId, owner.AccountId);

        using (os.Edge.Enter(os.Edge.Agent(alice, brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.GrantAsOwner(grant, ct));
        }

        using (os.Edge.Enter(os.Edge.Person(stranger.PrincipalId, stranger.AccountId, brain)))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => identity.GrantAsOwner(grant, ct));
        }

        Assert.Empty(await identity.ListGrants(owner.BrainId, ct));

        using (os.Edge.Enter(os.Edge.Person(owner.PrincipalId, owner.AccountId, brain)))
        {
            Assert.NotNull(await identity.GrantAsOwner(grant, ct));
        }
    }

    // The port never enters the kernel itself: its platform members run only when the host already stamped the kernel.
    [Fact]
    public async Task PlatformMembersRequireAKernelTheHostStamped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local();
        os.Register<DirectoryNeuron>();
        var port = new LocalDirectoryPort((contract, key) => LocalIdentityConformance.ResolveNeuron(os, contract, key), null, os.Callers, os.Edge);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => port.RegisterPrincipal("alice", "Alice", ct));
        using (os.Edge.Enter(os.Edge.Person("alice", "acc", new BrainId("brain-alice"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => port.RegisterPrincipal("alice", "Alice", ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => port.ListGrants("brain-alice", ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => port.Validate("s", ct));
        }

        using (os.Edge.EnterKernel(BrainId.Kernel))
        {
            Assert.Equal("alice", (await port.RegisterPrincipal("alice", "Alice", ct)).Member.PrincipalId);
        }
    }
}
