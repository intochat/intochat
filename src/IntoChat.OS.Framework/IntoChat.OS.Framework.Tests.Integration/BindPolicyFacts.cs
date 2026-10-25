using System.Net;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework.Tests.Integration;

// D-21: IntoChatOS:Topology (unset = local) decides where a process may listen; IntoChatOS:Bind widens it explicitly.
public sealed class BindPolicyFacts
{
    private const string Topology = IntoChatOSNames.TopologyKey;
    private const string Bind = IntoChatOSNames.BindKey;

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    [InlineData("local-cloud")]
    [InlineData("LOCAL")]
    public void UnsetLocalAndLocalCloudWithoutABindAreLoopbackOnly(string? topology)
    {
        var configuration = Configuration((Topology, topology));
        var policy = BindPolicy.From(configuration);
        Assert.Equal(BindKind.Loopback, policy.Kind);
        Assert.True(BindPolicy.IsLocal(configuration));
        Assert.Empty(BindPolicyKestrel.Disallowed(Configuration((Topology, topology), ("urls", "http://127.0.0.1:5080;http://[::1]:5081;http://localhost:5082")), policy));
        Assert.Equal(2, BindPolicyKestrel.Disallowed(Configuration((Topology, topology), ("urls", "http://0.0.0.0:5080;http://192.168.1.10:5081")), policy).Count);
    }

    [Fact]
    public void ALocalListenerBeyondLoopbackWithoutABindIsRefusedAtStartupNamingTheBind()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BindPolicyKestrel.Validate(Configuration(("ASPNETCORE_URLS", "http://+:5080"))));
        Assert.Contains(Bind, exception.Message);
        // The service defaults every server carries refuse it too, whatever the posture.
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["IntoChatOS:Auth:Posture"] = "Secured";
        builder.Configuration["Kestrel:Endpoints:Web:Url"] = "http://0.0.0.0:0";
        builder.Configuration[LegacyConfigurationGuard.IgnoreLegacyDataDirectoryKey] = "true";
        Assert.Throws<InvalidOperationException>(() => builder.AddServiceDefaults());
    }

    [Fact]
    public void AnEveryInterfaceBindAllowsEveryListener()
    {
        var configuration = Configuration((Bind, "0.0.0.0"), ("urls", "http://0.0.0.0:5080;http://*:5081;http://example.test:5082"));
        var policy = BindPolicy.From(configuration);
        Assert.Equal(BindKind.Explicit, policy.Kind);
        Assert.True(policy.HasNonLoopback);
        BindPolicyKestrel.Validate(configuration);
    }

    [Fact]
    public void ASpecificBindAllowsOnlyThatAddress()
    {
        var configuration = Configuration((Topology, "local-cloud"), (Bind, "192.168.1.10, [fe80::1]"), ("urls", "http://192.168.1.10:5080;http://[fe80::1]:5081;http://0.0.0.0:5082;http://10.0.0.2:5083"));
        var offenders = BindPolicyKestrel.Disallowed(configuration, BindPolicy.From(configuration));
        Assert.Equal(["http://0.0.0.0:5082", "http://10.0.0.2:5083"], offenders);
    }

    [Fact]
    public void ALoopbackBindIsNotBeyondLoopback()
        => Assert.False(BindPolicy.From(Configuration((Bind, "127.0.0.1,localhost,::1"))).HasNonLoopback);

    [Fact]
    public void CloudKeepsTheHostedRules()
    {
        var configuration = Configuration((Topology, "cloud"), ("ASPNETCORE_URLS", "http://0.0.0.0:8080"));
        Assert.Equal(BindKind.Hosted, BindPolicy.From(configuration).Kind);
        Assert.False(BindPolicy.IsLocal(configuration));
        BindPolicyKestrel.Validate(configuration);
    }

    [Fact]
    public void AContainerKeepsItsExemption()
    {
        var configuration = Configuration(("DOTNET_RUNNING_IN_CONTAINER", "true"), ("ASPNETCORE_URLS", "http://+:8080"));
        Assert.False(BindPolicy.IsLocal(configuration));
        BindPolicyKestrel.Validate(configuration);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("local+cloud")]
    public void AnUnknownTopologyStopsStartupNamingTheKeyAndTheAllowedValues(string topology)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BindPolicy.From(Configuration((Topology, topology))));
        Assert.Contains(Topology, exception.Message);
        Assert.Contains("local, local-cloud or cloud", exception.Message);
        Assert.DoesNotContain(topology, exception.Message.Replace("local, local-cloud or cloud", "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("everywhere")]
    [InlineData("1")]
    [InlineData("0.0.0.0,")]
    [InlineData("http://0.0.0.0")]
    public void AMalformedBindStopsStartupNamingTheKeyAndNeverFallsBackToEveryInterface(string bind)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BindPolicy.From(Configuration((Bind, bind))));
        Assert.Contains(Bind, exception.Message);
        Assert.Throws<InvalidOperationException>(() => BindPolicyKestrel.Validate(Configuration((Bind, bind), ("urls", "http://0.0.0.0:0"))));
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationManager();
        foreach (var (key, value) in values) { configuration[key] = value; }
        return configuration;
    }
}
