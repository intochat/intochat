using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Kernel.OsRegistry;
using Xunit;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

public sealed class RegistryBrokerFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("settings://theme", RegistryTier.Settings, "theme")]
    [InlineData("settings://editor/tabSize", RegistryTier.Settings, "editor/tabSize")]
    [InlineData("env://APP_ENV", RegistryTier.Environment, "APP_ENV")]
    [InlineData("vault://github/pat", RegistryTier.Vault, "github/pat")]
    public void ParseUri_ExtractsExpectedTierAndKey(string uri, RegistryTier expectedTier, string expectedKey)
    {
        var (tier, key) = RegistryBroker.ParseUri(uri);
        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseUri_ThrowsArgumentExceptionForNullOrWhitespace(string uri)
    {
        Assert.Throws<ArgumentException>(() => RegistryBroker.ParseUri(uri));
    }

    [Theory]
    [InlineData("invalid-uri")]
    [InlineData("settings://")]
    public void ParseUri_ThrowsFormatExceptionForMalformedUri(string uri)
    {
        Assert.Throws<FormatException>(() => RegistryBroker.ParseUri(uri));
    }

    [Theory]
    [InlineData("http://example.com/api")]
    [InlineData("custom://foo/bar")]
    public void ParseUri_ThrowsNotSupportedExceptionForUnsupportedSchemes(string uri)
    {
        Assert.Throws<NotSupportedException>(() => RegistryBroker.ParseUri(uri));
    }

    [Fact]
    public async Task SettingsTier_RoundtripsValuesAndListsDescriptors()
    {
        var broker = new RegistryBroker();

        await broker.SetAsync("settings://theme", "nord-dark", ct: Ct);
        await broker.SetAsync("settings://editor/fontSize", "14", ct: Ct);

        var theme = await broker.GetAsync("settings://theme", ct: Ct);
        Assert.Equal("nord-dark", theme);

        var fontSize = await broker.GetAsync<int>("settings://editor/fontSize", ct: Ct);
        Assert.Equal(14, fontSize);

        var exists = await broker.ExistsAsync("settings://theme", ct: Ct);
        Assert.True(exists);

        var entries = await broker.ListAsync("settings", ct: Ct);
        Assert.Contains(entries, e => e.Tier == RegistryTier.Settings && e.Key == "editor/fontSize");

        await broker.RemoveAsync("settings://theme", ct: Ct);
        Assert.False(await broker.ExistsAsync("settings://theme", ct: Ct));
    }

    [Fact]
    public async Task EnvironmentTier_RoundtripsValuesAndListsDescriptors()
    {
        var broker = new RegistryBroker();

        await broker.SetAsync("env://LOG_LEVEL", "Debug", ct: Ct);
        await broker.SetAsync("env://MAX_CONCURRENCY", "8", ct: Ct);

        var logLevel = await broker.GetAsync("env://LOG_LEVEL", ct: Ct);
        Assert.Equal("Debug", logLevel);

        var maxConcurrency = await broker.GetAsync<int>("env://MAX_CONCURRENCY", ct: Ct);
        Assert.Equal(8, maxConcurrency);

        var entries = await broker.ListAsync("env", ct: Ct);
        Assert.Contains(entries, e => e.Tier == RegistryTier.Environment && e.Key == "LOG_LEVEL");
        Assert.Contains(entries, e => e.Tier == RegistryTier.Environment && e.Key == "MAX_CONCURRENCY");
    }

    [Fact]
    public async Task VaultTier_EnforcesZeroRawTokenLeakageForNonPlatformCaller()
    {
        var broker = new RegistryBroker();
        const string secretValue = "ghp_ultra_secret_platform_access_token_123456789";

        // Write secret into vault tier
        await broker.SetAsync("vault://tokens/github", secretValue, ct: Ct);

        // 1. Non-platform read must be masked and never leak raw token
        var maskedRead = await broker.GetAsync("vault://tokens/github", ct: Ct);
        Assert.NotNull(maskedRead);
        Assert.DoesNotContain("secret_platform_access_token", maskedRead, StringComparison.Ordinal);
        Assert.Contains("••••••••", maskedRead, StringComparison.Ordinal);
        Assert.EndsWith("6789", maskedRead, StringComparison.Ordinal);

        // 2. Listing for non-platform caller exposes masked value and tail
        var descriptors = await broker.ListAsync("vault", ct: Ct);
        var secretDescriptor = Assert.Single(descriptors, d => d.Key == "tokens/github");
        Assert.Equal(RegistryTier.Vault, secretDescriptor.Tier);
        Assert.Equal("6789", secretDescriptor.MaskedTail);
        Assert.DoesNotContain(secretValue, secretDescriptor.Value, StringComparison.Ordinal);

        // 3. Platform caller context reads the genuine unmasked secret
        var platformCaller = CapabilityCallers.Platform("platform_sys", "acc_root", "brain_sys");
        using var stamp = RuntimeCallerContext.StampScope(platformCaller);

        var unmaskedRead = await broker.GetAsync("vault://tokens/github", ct: Ct);
        Assert.Equal(secretValue, unmaskedRead);
    }

    [Fact]
    public async Task TypedConversion_HandlesPrimitivesAndComplexTypes()
    {
        var broker = new RegistryBroker();

        await broker.SetAsync("settings://features/offline", "true", ct: Ct);
        var isOffline = await broker.GetAsync<bool>("settings://features/offline", ct: Ct);
        Assert.True(isOffline);

        var complexConfig = new SampleConfig("production", 42, true);
        await broker.SetAsync("settings://app/config", JsonSerializer.Serialize(complexConfig), ct: Ct);

        var readConfig = await broker.GetAsync<SampleConfig>("settings://app/config", ct: Ct);
        Assert.NotNull(readConfig);
        Assert.Equal("production", readConfig.Environment);
        Assert.Equal(42, readConfig.Workers);
        Assert.True(readConfig.TracingEnabled);
    }

    private sealed record SampleConfig(string Environment, int Workers, bool TracingEnabled);
}
