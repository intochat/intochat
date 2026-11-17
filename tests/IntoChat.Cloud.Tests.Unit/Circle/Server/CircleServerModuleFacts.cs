using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Circle.Server.Tests;

// R4-01, CLOUD-02: the circle server module is composed by the cloud host and declares what it needs.
public sealed class CircleServerModuleFacts
{
    [Theory]
    [InlineData("cloud")]
    [InlineData("Cloud")]
    [InlineData("local")]
    [InlineData("local+cloud")]
    [InlineData("")]
    [InlineData(null)]
    public void CircleServerLoadsRegardlessOfTopology(string? topology)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntoChatOS:Topology"] = topology }).Build();
        Assert.NotNull(CircleServerModule.For(configuration));
        Assert.True(IntoChat.OS.Framework.Modules.OsModule.FromDigital(typeof(CircleServerModule)).LoadsUnder(topology));
    }

    [Fact]
    public void TheManifestNamesTheModuleAndItsNeeds()
    {
        var manifest = ModuleManifest.Of(typeof(CircleServerModule));
        Assert.Equal("circle-server", manifest.Id);
        Assert.Equal([CapabilityId.Parse("identity@1")], manifest.Requires);
        Assert.Contains(Need.Http, manifest.Needs);
        Assert.Contains(Need.State("cluster"), manifest.Needs);
        Assert.Equal(typeof(CircleServerOptions), Assert.Single(manifest.Settings).OptionsType);
    }

    [Fact]
    public void TheDefaultsAreTheD09LifetimesAndTheT1003Limits()
    {
        var options = new CircleServerOptions();
        Assert.Equal(TimeSpan.FromMinutes(15), options.AccessLifetime);
        Assert.Equal(TimeSpan.FromDays(30), options.RefreshLifetime);
        Assert.Equal((10, TimeSpan.FromMinutes(10)), (options.EnrollPerAddress, options.EnrollPerAddressWindow));
        Assert.Equal((30, TimeSpan.FromHours(1)), (options.EnrollPerCircle, options.EnrollPerCircleWindow));
    }

    [Fact]
    public void TokensRouteToTheirCredentialAndCredentialIdsAreStablePerMachine()
    {
        var id = MachineTokens.CredentialIdFor("circle", "machine");
        Assert.Equal(id, MachineTokens.CredentialIdFor("circle", "machine"));
        Assert.NotEqual(id, MachineTokens.CredentialIdFor("circle", "machine-2"));
        Assert.NotEqual(id, MachineTokens.CredentialIdFor("circle\nmachine", ""));
        var token = MachineTokens.New(id);
        Assert.NotEqual(token, MachineTokens.New(id));
        Assert.True(MachineTokens.TryCredentialId(token, out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(MachineTokens.TryCredentialId("short.token", out _));
        Assert.False(MachineTokens.TryCredentialId(null, out _));
        Assert.False(MachineTokens.TryCredentialId(id + "." + new string('a', 200), out _));
    }

    [Fact]
    public void TheNonceCacheRefusesASeenNonceUntilItExpires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new NonceCache(clock);
        var expires = clock.GetUtcNow().AddMinutes(2);
        Assert.True(cache.TryAdd("n1", expires));
        Assert.False(cache.TryAdd("n1", expires));
        Assert.True(cache.TryAdd("n2", expires));
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(cache.TryAdd("n1", clock.GetUtcNow().AddMinutes(2)));
    }

    [Fact]
    public void RateLimitsArePartitionedByAddressAndByCircle()
    {
        using var limits = new EnrollmentRateLimits(Options.Create(new CircleServerOptions { EnrollPerAddress = 2, EnrollPerCircle = 1 }));
        Assert.True(limits.TryAddress("10.0.0.1"));
        Assert.True(limits.TryAddress("10.0.0.1"));
        Assert.False(limits.TryAddress("10.0.0.1"));
        Assert.True(limits.TryAddress("10.0.0.2"));
        Assert.True(limits.TryCircle("a"));
        Assert.False(limits.TryCircle("a"));
        Assert.True(limits.TryCircle("b"));
    }

    [Fact]
    public void ClockDoesNotThrowWhenProviderIsNotKeyed()
    {
        var nonKeyed = new NonKeyedServiceProvider();
        Assert.Same(TimeProvider.System, CircleServerModule.Clock(nonKeyed));
    }

    private sealed class NonKeyedServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
