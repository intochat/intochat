using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantShapeFacts
{
    [Fact]
    public void TupleFactorySetsCanonicalAndDerivedLegacyFields()
    {
        var g = Grant.Tuple("brain:vlad", "contacts/friends#member", "apps/photocraft", "run");
        Assert.Equal("brain:vlad", g.Owner); Assert.Equal("vlad", g.WorkspaceId);
        Assert.Equal("contacts/friends#member", g.Grantee);
        Assert.Equal("apps/photocraft", g.Target); Assert.Equal("apps/photocraft", g.TargetNeuronKey);
        Assert.Equal("run", g.Scope); Assert.Equal("run", g.SemanticTypeId);
        Assert.False(g.Delegable); Assert.Equal(32, g.Id.Length);
    }

    [Fact]
    public void NormalizeFillsCanonicalFromLegacyAndIsIdempotent()
    {
        var legacy = new Grant { AppId = "photocraft", SemanticTypeId = "person.birthDate", WorkspaceId = "vlad", TargetNeuronKey = "vlad/person/me" };
        var n = Grant.Normalize(legacy);
        Assert.Equal("brain:vlad", n.Owner); Assert.Equal("app:photocraft", n.Grantee);
        Assert.Equal("vlad/person/me", n.Target); Assert.Equal("person.birthDate", n.Scope);
        Assert.True(n.IsCanonical); Assert.Same(n, Grant.Normalize(n));
    }

    [Fact]
    public void UseViaIsRecognizedAndNeverDelegable()
    {
        Assert.True(GrantRelations.IsUseVia("use-via:apps/photocraft"));
        var g = Grant.Tuple("brain:vlad", "brain:bob", "vlad/connection/github/main", "use-via:apps/photocraft", delegable: true);
        Assert.False(g.Delegable);
    }
}
