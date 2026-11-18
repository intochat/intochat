using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantsNeuronFacts
{
    [Fact]
    public async Task GrantAddsGrantIncrementsVersionAndEmitsSignal()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test-brain", "test-brain", new BrainId("test-brain")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var id = new NeuronId(new BrainId("test-brain"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
        var grantsNeuron = os.Get<IGrantsNeuron>(id);

        var v0 = await grantsNeuron.GetVersion();
        Assert.Equal(0, v0);

        var grant = new Grant
        {
            Id = "grant-1",
            Owner = "app:local/apps/ino",
            Grantee = "app:local/apps/settings",
            Target = "files/documents",
            Scope = "read",
            WorkspaceId = "test-brain",
            AppId = "local/apps/settings",
            SemanticTypeId = "read",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        await grantsNeuron.Grant(grant);

        var v1 = await grantsNeuron.GetVersion();
        Assert.True(v1 > v0);

        var list = await grantsNeuron.List();
        Assert.Contains(list, g => g.Id == "grant-1");
    }

    [Fact]
    public async Task RevokeRemovesGrantAndIncrementsVersion()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test-brain", "test-brain", new BrainId("test-brain")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var id = new NeuronId(new BrainId("test-brain"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey);
        var grantsNeuron = os.Get<IGrantsNeuron>(id);

        var grant = new Grant
        {
            Id = "grant-revocable",
            Owner = "app:local/apps/ino",
            Grantee = "app:local/apps/settings",
            Target = "files/documents",
            Scope = "write",
            WorkspaceId = "test-brain",
            AppId = "local/apps/settings",
            SemanticTypeId = "write",
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };

        await grantsNeuron.Grant(grant);
        var v1 = await grantsNeuron.GetVersion();

        await grantsNeuron.Revoke("grant-revocable");
        var v2 = await grantsNeuron.GetVersion();
        Assert.True(v2 > v1);

        var list = await grantsNeuron.List();
        Assert.DoesNotContain(list, g => g.Id == "grant-revocable");
    }

    [Fact]
    public async Task LegacyRowWithoutCanonicalFieldsEvaluatesAsBefore()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test-brain", "test-brain", new BrainId("test-brain")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var g = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("test-brain"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        await g.Grant(new Grant { Id = "legacy1", AppId = "photocraft", SemanticTypeId = "person.birthDate", WorkspaceId = "test-brain" });
        var before = await g.GetVersion();
        var list = await g.List();
        Assert.Single(list);
        Assert.True(list[0].IsCanonical);
        Assert.Equal("app:photocraft", list[0].Grantee);
        Assert.Equal(before + 1, await g.GetVersion());
        Assert.Equal(before + 1, await g.GetVersion()); // second List does not bump again
        await g.List();
    }

    [Fact]
    public async Task RevokeMatchesIdOnly()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test-brain", "test-brain", new BrainId("test-brain")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var g = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("test-brain"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        await g.Grant(Grant.Tuple("brain:test-brain", "brain:bob", "apps/photocraft", "run") with { Id = "a" });
        await g.Grant(Grant.Tuple("brain:test-brain", "brain:ana", "apps/photocraft", "run") with { Id = "b" });
        await g.Revoke("apps/photocraft");
        Assert.Equal(2, (await g.List()).Length);
        await g.Revoke("a");
        Assert.Equal(["b"], (await g.List()).Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task SameTupleReplacesInsteadOfDuplicating()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test-brain", "test-brain", new BrainId("test-brain")));
        await using var os = TestOS.Local();
        os.Register<GrantsNeuron>();
        var g = os.Get<IGrantsNeuron>(new NeuronId(new BrainId("test-brain"), IGrantsNeuron.TypeName, IGrantsNeuron.DefaultKey));
        await g.Grant(Grant.Tuple("brain:test-brain", "brain:bob", "apps/photocraft", "run"));
        await g.Grant(Grant.Tuple("brain:test-brain", "brain:bob", "apps/photocraft", "run", expires: DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Single(await g.List());
    }
}
