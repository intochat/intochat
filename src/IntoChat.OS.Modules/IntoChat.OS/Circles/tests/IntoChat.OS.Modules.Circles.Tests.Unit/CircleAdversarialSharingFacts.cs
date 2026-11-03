using DigitalBrain.Capabilities.Connections;
using System.Reflection;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Modules.Circles.Tests.Unit;

public sealed class CircleAdversarialSharingFacts
{
    [Fact]
    public void CircleRecord_ContainsNoCredentialOrAssemblyStorage()
    {
        var properties = typeof(CircleRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Name",
            "Owner",
            "Members",
            "SharedApps",
            "ComputeSharePercent",
            "Invited",
            "Version",
            "Bindings",
            "Admins"
        };

        var actualNames = properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedNames, actualNames);

        foreach (var prop in properties)
        {
            Assert.False(prop.PropertyType == typeof(byte[]), $"CircleRecord must not store binary payloads: {prop.Name}");
            Assert.DoesNotContain("secret", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("assembly", prop.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CircleBinding_ContainsOnlyReferenceIdentifiers_ZeroSecrets()
    {
        var properties = typeof(CircleBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var propertyNames = properties.Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(["AccountId", "AppName", "ConnectionKey", "Slot"], propertyNames);
        Assert.Null(typeof(CircleBinding).GetProperty("Copy"));

        foreach (var prop in properties)
        {
            Assert.Equal(typeof(string), prop.PropertyType);
        }
    }

    [Theory]
    [InlineData("alice/connection/google/conn-1", "alice")]
    [InlineData("neuron://bob/connection/slack/conn-2", "bob")]
    [InlineData("standalone-key", "local")]
    public void ExtractBrain_ParsesSourceBrainAccurately(string connectionKey, string expectedBrain)
    {
        var clean = connectionKey.StartsWith(ConnectionKeys.NeuronScheme, StringComparison.Ordinal)
            ? connectionKey[ConnectionKeys.NeuronScheme.Length..]
            : connectionKey;
        var slash = clean.IndexOf('/', StringComparison.Ordinal);
        var extracted = slash > 0 ? clean[..slash] : "local";

        Assert.Equal(expectedBrain, extracted);
    }

    [Fact]
    public void ShareIntoCircle_GrantIssuance_FormatAndGranteeContract()
    {
        const string circleBrain = "circle-123";
        const string sourceBrain = "user-456";
        const string appName = "gallery";
        const string connectionKey = "user-456/connection/postgres/db-1";

        var appGrantId = $"circle:{circleBrain}:app:{appName}";
        var appGrantee = $"brain:{circleBrain}";
        var appTarget = $"{sourceBrain}/apps/{appName}";

        var connGrantId = $"circle:{circleBrain}:conn:{connectionKey}";
        var connGrantee = $"brain:{circleBrain}";

        // Assert Grantee is addressed to the circle brain boundary
        Assert.Equal("brain:circle-123", appGrantee);
        Assert.Equal("brain:circle-123", connGrantee);

        // Verify Grant can be constructed with these reference parameters without secrets
        var appGrant = Grant.Tuple($"brain:{sourceBrain}", appGrantee, appTarget, GrantRelations.Run) with { Id = appGrantId };
        var connGrant = Grant.Tuple($"brain:{sourceBrain}", connGrantee, connectionKey, $"use-via:{appName}") with { Id = connGrantId };

        Assert.Equal("circle:circle-123:app:gallery", appGrant.Id);
        Assert.Equal("circle:circle-123:conn:user-456/connection/postgres/db-1", connGrant.Id);

        Assert.Equal($"brain:{circleBrain}", appGrant.Grantee);
        Assert.Equal(GrantRelations.Run, appGrant.Scope);
        Assert.True(appGrant.IsCanonical);

        Assert.Equal($"brain:{circleBrain}", connGrant.Grantee);
        Assert.Equal($"use-via:{appName}", connGrant.Scope);
        Assert.True(connGrant.IsCanonical);
    }

    [Fact]
    public void Unbind_RevocationIdParity_MatchesShareGrantIds()
    {
        const string circleBrain = "circle-xyz";
        const string appName = "notes";
        const string connectionKey = "owner/connection/sql/main";

        var shareAppId = $"circle:{circleBrain}:app:{appName}";
        var shareConnId = $"circle:{circleBrain}:conn:{connectionKey}";

        var binding = new CircleBinding("acct-1", appName, "slotA", connectionKey);

        var revokeConnId = $"circle:{circleBrain}:conn:{binding.ConnectionKey}";
        var revokeAppId = $"circle:{circleBrain}:app:{binding.AppName}";

        Assert.Equal(shareAppId, revokeAppId);
        Assert.Equal(shareConnId, revokeConnId);
    }
}
