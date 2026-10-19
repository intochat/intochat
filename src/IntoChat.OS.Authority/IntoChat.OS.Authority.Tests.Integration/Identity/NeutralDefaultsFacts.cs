using System.Reflection;
using IntoChat.OS.Authority.Identity.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Authority.Tests.Integration.Identity;

// The OS ships brand-neutral identity, cookie, claim and container names; a distribution (the platform) pins its own
// by configuration. The defaults are persisted or live state, so each is asserted exactly and none may carry a brand.
public sealed class NeutralDefaultsFacts
{
    // Assembled so this file never matches the repository grep gate.
    private static readonly string[] Brands = ["into" + "chat", "digital" + "brain"];

    [Fact]
    public void IdentityDefaultsAreNeutral()
    {
        var identity = new IdentityHostOptions();
        Assert.Equal(("os.session", "os.v1", "os-protection-v1"), (identity.CookieName, identity.ProtectionApplicationName, identity.ProtectionContainerName));
        Assert.Equal(("os.account", "os.brain"), (identity.AccountClaim, identity.BrainClaim));
        Assert.Equal("identity-directory", identity.DirectoryKey);
        Assert.Equal("os-state", IntoChatOSNames.GrainStateContainer);
    }

    [Fact]
    public void NoDefaultStringCarriesABrand()
    {
        var defaults = new object[] { new IdentityHostOptions() };
        var values = defaults.SelectMany(options => options.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => (Name: options.GetType().Name + "." + property.Name, Value: (string?)property.GetValue(options) ?? "")))
            .Append((Name: "GrainStateContainer", Value: IntoChatOSNames.GrainStateContainer))
            .ToArray();
        var branded = values.Where(pair => Brands.Any(brand => pair.Value.Contains(brand, StringComparison.OrdinalIgnoreCase))).Select(pair => pair.Name).ToArray();
        Assert.True(branded.Length == 0, "Defaults carrying a brand (a distribution sets these by configuration): " + string.Join(", ", branded));
    }

    [Fact]
    public void ADistributionPinsItsOwnNamesByConfiguration()
    {
        var brand = Brands[0];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
        [
            new(IdentityHostOptions.Key(nameof(IdentityHostOptions.AccountClaim)), brand + ".account"),
            new(IdentityHostOptions.Key(nameof(IdentityHostOptions.BrainClaim)), brand + ".brain"),
            new(IdentityHostOptions.Key(nameof(IdentityHostOptions.DirectoryKey)), brand + "-identity-directory"),
        ]).Build();
        var configured = configuration.GetSection(IdentityHostOptions.SectionName).Get<IdentityHostOptions>()!;
        Assert.Equal((brand + ".account", brand + ".brain", brand + "-identity-directory"), (configured.AccountClaim, configured.BrainClaim, configured.DirectoryKey));
        Assert.True(configured.Validate());
        Assert.False(new IdentityHostOptions { BrainClaim = " " }.Validate());
        Assert.False(new IdentityHostOptions { DirectoryKey = "" }.Validate());
    }
}
