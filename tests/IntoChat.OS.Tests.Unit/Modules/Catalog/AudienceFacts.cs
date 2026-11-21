using System.Text.Json;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Modules.Catalog.Tests.Unit;

public sealed class AudienceFacts
{
    [Theory]
    [InlineData("", typeof(Audience.Public))]
    [InlineData("public", typeof(Audience.Public))]
    [InlineData("friends", typeof(Audience.Friends))]
    [InlineData("close-friends", typeof(Audience.Only))]
    [InlineData("team", typeof(Audience.Only))]
    public void LegacyCircleLabelsMapToAnAudience(string circle, Type expected)
        => Assert.IsType(expected, Audience.FromLegacy(circle));

    [Fact]
    public void AnOldPostWithACircleStillLoads()
    {
        var post = JsonSerializer.Deserialize(
            """{"id":"p1","content":"x","createdAt":"2026-01-01T00:00:00+00:00","circle":"team"}""",
            CatalogContractsJson.Default.ProfilePostItem)!;
        Assert.Empty(Assert.IsType<Audience.Only>(post.Seen).Accounts);
    }

    [Fact]
    public void ANewPostWritesItsAudience()
    {
        var json = JsonSerializer.Serialize(
            new ProfilePostItem("p", "c", default, "") { Audience = new Audience.Friends() },
            CatalogContractsJson.Default.ProfilePostItem);
        Assert.Contains("\"audience\":{\"kind\":\"friends\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAudienceRoundTripsNamedAccounts()
    {
        var original = new ProfilePostItem("p2", "secret note", default, "")
        {
            Audience = new Audience.Only([new AccountId("ben"), new AccountId("claire")]),
        };
        var json = JsonSerializer.Serialize(original, CatalogContractsJson.Default.ProfilePostItem);
        var restored = JsonSerializer.Deserialize(json, CatalogContractsJson.Default.ProfilePostItem)!;
        var only = Assert.IsType<Audience.Only>(restored.Seen);
        Assert.Equal([new AccountId("ben"), new AccountId("claire")], only.Accounts);
    }
}
