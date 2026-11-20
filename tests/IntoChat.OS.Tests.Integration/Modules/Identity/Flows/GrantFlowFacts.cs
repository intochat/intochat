using IntoChat.OS.Tests;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Identity.Contracts.Grants;

namespace IntoChat.OS.Authority.Tests.Flows;

public sealed class GrantFlowFacts(IdentityFixture fixture) : IClassFixture<IdentityFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    public static TheoryData<NeuronRuntimeKind> Kinds => IdentityFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task GrantThenRevokeIsVisibleThroughGrantsChangedVersions(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id(IGrantsNeuron.TypeName, "grants/flow-" + kind);
        var grants = runtime.Get<IGrantsNeuron>(id);

        var grant = Grant.Tuple("brain:facts", "app:facts/apps/demo", "facts/apps/demo", "os.notes") with
        {
            Id = "grant-flow-" + kind,
        };

        await grants.Grant(grant);
        Assert.Equal(1L, await grants.GetVersion());
        Assert.Single(await grants.List(), g => g.Id == grant.Id);

        await grants.Revoke(grant.Id);
        Assert.Equal(2L, await grants.GetVersion());
        Assert.DoesNotContain(await grants.List(), g => g.Id == grant.Id);

        var signals = await runtime.WaitForSignals<GrantsChanged>(id, 2, Within, ct);
        Assert.Equal([1L, 2L], signals.Select(s => s.Version).ToArray());
    }
}
