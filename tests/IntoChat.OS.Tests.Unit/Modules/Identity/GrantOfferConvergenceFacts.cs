using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Components;
using Xunit;

namespace IntoChat.OS.Modules.Identity.Tests.Unit.Identity;

public sealed class GrantOfferConvergenceFacts
{
    [Fact]
    public void LegacyPersistedGrantJsonDeserializesUnchangedAndProjectsToUnifiedOffer()
    {
        const string legacyJson = """
            {
              "AppId": "customer-researcher",
              "SemanticTypeId": "person.birthDate",
              "Mode": 2,
              "WorkspaceId": "ada",
              "Revoked": false
            }
            """;

        var grant = JsonSerializer.Deserialize<Grant>(legacyJson);

        Assert.NotNull(grant);
        Assert.Equal("customer-researcher", grant.AppId);
        Assert.Equal("person.birthDate", grant.SemanticTypeId);
        Assert.Equal(GrantMode.Always, grant.Mode);
        Assert.Equal("ada", grant.WorkspaceId);
        Assert.False(grant.Revoked);
        Assert.True(grant.Covers("customer-researcher", "person.birthDate", "ada", conversationId: null));

        // Additive unified projections work without mutating persisted fields.
        Assert.Equal("app:customer-researcher", grant.Subject);
        Assert.Equal("viewer", grant.RoleName);
        Assert.Equal(Role.Viewer, grant.EffectiveRole());

        var offer = grant.ToOffer();
        Assert.NotNull(offer);
        Assert.Equal(new NeuronId(new BrainId("ada"), "semantic.grant", "person.birthDate"), offer.Resource);
        Assert.Equal("app:customer-researcher", offer.To);
        Assert.Equal(Role.Viewer, offer.Role);

        // Revoked legacy grant projects to no active offer.
        Assert.Null((grant with { Revoked = true }).ToOffer());
    }

    [Fact]
    public void OfferRoundTripsThroughCanonicalGrant()
    {
        var resource = new NeuronId(new BrainId("ada"), "postgres.table", "research/notes");
        var offer = new Offer(resource, "ben", Role.Editor);

        var grant = GrantOfferProjection.FromOffer(offer);
        var projected = grant.ToOffer();

        Assert.True(grant.IsCanonical);
        Assert.Equal("ben", grant.Subject);
        Assert.Equal("editor", grant.RoleName);
        Assert.Equal(Role.Editor, grant.EffectiveRole());
        Assert.Equal(offer, projected);
    }
}
