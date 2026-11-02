using DigitalBrain;
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
}
