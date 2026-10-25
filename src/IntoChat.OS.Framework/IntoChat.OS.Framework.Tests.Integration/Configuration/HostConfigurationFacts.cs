using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Xunit;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class HostConfigurationFacts
{
    private static readonly IntegrationSchema Schema = IntegrationSchema.For("sample", "Sample").RequiresSecret("ConnectionString").OffersSetting("Region");

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    [Fact]
    public void ParameterReadsTheAspireParametersSection()
        => Assert.Equal("v", HostConfiguration.Parameter(Config(("Parameters:db", "v")), "db"));

    [Fact]
    public void IntegrationValueReadsADeclaredField()
        => Assert.Equal("c", HostConfiguration.IntegrationValue(Config((Schema.Key("ConnectionString"), "c")), Schema, "ConnectionString"));

    [Fact]
    public void IntegrationValueRefusesAnUndeclaredField()
        => Assert.Throws<ArgumentException>(() => HostConfiguration.IntegrationValue(Config((Schema.Key("Other"), "x")), Schema, "Other"));

    [Fact]
    public void OtlpEndpointReadsTheStandardVariable()
        => Assert.Equal("http://otel", HostConfiguration.OtlpEndpoint(Config((HostConfiguration.OtlpEndpointVariable, "http://otel"))));

    [Fact]
    public void ConnectionStringReadsTheConnectionStringsSection()
        => Assert.Equal("cs", HostConfiguration.ConnectionString(Config(("ConnectionStrings:main", "cs")), "main"));

    [Fact]
    public void HostKeyReadsAnIntoChatOSNamesKey()
        => Assert.Equal("Tpm", HostConfiguration.HostKey(Config((IntoChatOSNames.CustodyConfigurationKey, "Tpm")), IntoChatOSNames.CustodyConfigurationKey));

    [Fact]
    public void HostKeyRefusesAnyOtherKey()
        => Assert.Throws<ArgumentException>(() => HostConfiguration.HostKey(Config(("Some:Key", "x")), "Some:Key"));

    [Fact]
    public void VariableRefusesAConfigurationPath()
    {
        Assert.Equal("id", HostConfiguration.Variable(Config(("AZURE_CLIENT_ID", "id")), "AZURE_CLIENT_ID"));
        Assert.Throws<ArgumentException>(() => HostConfiguration.Variable(Config(), "A:B"));
    }

    [Fact]
    public void IsModuleOptionSetSeesOnlyExplicitValues()
    {
        var key = IntoChatOSConfiguration.ModuleOptionsSection("orleans") + ":GrainStateContainer";
        Assert.True(HostConfiguration.IsModuleOptionSet(Config((key, "c")), "orleans", "GrainStateContainer"));
        Assert.False(HostConfiguration.IsModuleOptionSet(Config(), "orleans", "GrainStateContainer"));
    }
}
