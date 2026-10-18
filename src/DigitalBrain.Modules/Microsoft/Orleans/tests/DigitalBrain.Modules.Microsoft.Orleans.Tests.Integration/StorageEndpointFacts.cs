using DigitalBrain.Microsoft.Orleans;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Modules.Microsoft.Orleans.Tests.Integration;

public sealed class StorageEndpointFacts
{
    private const string Field = OrleansStorageHosting.ClusteringField;

    [Fact]
    public void NeitherValueIsAbsent() => Assert.Null(StorageEndpoint.Select(Field, null, " "));

    [Fact]
    public void AConnectionStringStaysAConnectionString()
    {
        var endpoint = StorageEndpoint.Select(Field, "UseDevelopmentStorage=true", null)!;
        Assert.Equal("UseDevelopmentStorage=true", endpoint.ConnectionString);
        Assert.Null(endpoint.ServiceUri);
    }

    [Fact]
    public void TheUriKeySelectsTheIdentity()
    {
        var endpoint = StorageEndpoint.Select(Field, null, "https://st.table.core.windows.net/")!;
        Assert.Equal(new Uri("https://st.table.core.windows.net/"), endpoint.ServiceUri);
        Assert.Null(endpoint.ConnectionString);
    }

    [Fact]
    public void AnHttpsUriInTheConnectionKeySelectsTheIdentity()
    {
        var endpoint = StorageEndpoint.Select(Field, "https://st.blob.core.windows.net/", null)!;
        Assert.Equal(new Uri("https://st.blob.core.windows.net/"), endpoint.ServiceUri);
    }

    [Fact]
    public void BothValuesRefuseToStartNamingTheStore()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            StorageEndpoint.Select(Field, "UseDevelopmentStorage=true", "https://st.table.core.windows.net/"));
        Assert.Contains("Clustering", error.Message, StringComparison.Ordinal);
        Assert.Contains("ClusteringUri", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ftp://st.table.core.windows.net/")]
    [InlineData("/relative")]
    [InlineData("http://st.table.core.windows.net/")]
    public void ANonHttpUriRefusesToStart(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => StorageEndpoint.Select(Field, null, value));
        Assert.Contains("ClusteringUri", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationReadsTheUriVariantKey()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(OrleansStorageHosting.RemindersField + StorageEndpoint.UriSuffix)] = "https://st.table.core.windows.net/",
        }).Build();
        Assert.NotNull(StorageEndpoint.Read(configuration, OrleansStorageHosting.RemindersField)!.ServiceUri);
    }

    [Theory]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1")]
    [InlineData("http://localhost:10000/devstoreaccount1")]
    public void PlainHttpIsAcceptedOnlyForALoopbackEmulator(string value) =>
        Assert.Equal(new Uri(value), StorageEndpoint.Select(Field, null, value)!.ServiceUri);

    [Fact]
    public void TheIntegrationClientIdSelectsAUserAssignedIdentityOverTheEnvironment()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OrleansModule.Integration.Key(StorageEndpoint.ManagedIdentityClientIdField)] = "integration-id",
            [StorageEndpoint.ClientIdVariable] = "environment-id",
        }).Build();
        Assert.Equal("integration-id", StorageEndpoint.CredentialOptions(configuration).ManagedIdentityClientId);
    }

    [Fact]
    public void AzureClientIdIsTheFallbackClientId()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StorageEndpoint.ClientIdVariable] = "environment-id",
        }).Build();
        Assert.Equal("environment-id", StorageEndpoint.CredentialOptions(configuration).ManagedIdentityClientId);
    }
}
