using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Supabase.Tests;

public sealed class BackingStoreFacts
{
    [Fact]
    public async Task InfrastructureHealthUsesTheDeploymentHandleWithoutAUserConnection()
    {
        Orleans.Runtime.RequestContext.Clear();
        var credentials = new MissingInfrastructure("Host=127.0.0.1;Port=1;Database=deployment_store;Username=reader;Timeout=1");
        await using var backing = new SupabaseBackingStore(credentials, Options.Create(new SupabaseModuleOptions { UseBackingStore = true }));
        var health = new SupabaseHealthCheck(backing, Options.Create(new SupabaseModuleOptions { UseBackingStore = true }));
        var ct = TestContext.Current.CancellationToken;
        var result = await health.CheckHealthAsync(new HealthCheckContext(), ct);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("deployment_store", result.Description, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new HealthCheckContext(), ct)).Status);
        Assert.Equal(1, credentials.Releases);
        Assert.Equal(("supabase", "Release", ct), credentials.Request);
    }

    [Fact]
    public async Task BackingStoreQueryUsesItsDeploymentCredentialWithoutAUserStamp()
    {
        Orleans.Runtime.RequestContext.Clear();
        var credentials = new MissingInfrastructure();
        await using var backing = new SupabaseBackingStore(credentials, Options.Create(new SupabaseModuleOptions { UseBackingStore = true }));
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => backing.QueryAsync("select 1", 1, ct));
        Assert.Equal(("supabase", "Release", ct), credentials.Request);
    }

    [Fact]
    public async Task UserOnlyModuleStartsAndHealthDoesNotRequireDeploymentCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<SupabaseModule>().StartAsync(ct);
        var options = brain.SiloServices.GetRequiredService<IOptions<SupabaseModuleOptions>>();
        Assert.False(options.Value.UseBackingStore);
        var check = new SupabaseHealthCheck(brain.SiloServices.GetRequiredService<ISupabaseBackingStore>(), options);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), ct)).Status);
        Assert.Empty(SupabaseModule.Connector.RequiredIntegrations);
        Assert.Equal(["supabase"], SupabaseModule.Connector.OptionalIntegrations);
    }

    [Fact]
    public async Task SelectedBackingStoreRejectsMissingDeploymentCredentialsBeforeStartupCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var brain = await ModuleTest.Create()
                .WithModule<SupabaseModule, SupabaseModuleOptions>(options => options.UseBackingStore = true).StartAsync(ct);
        });
        Assert.Contains("Supabase backing integration is unavailable", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class MissingInfrastructure(string? connection = null) : IModuleIntegrations<SupabaseModule>, IIntegrationHandle
    {
        public string IntegrationId => "supabase";
        public int Releases { get; private set; }
        public (string, string, CancellationToken) Request { get; private set; }
        public IIntegrationHandle Get(string integrationId) { Assert.Equal(IntegrationId, integrationId); return this; }
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            Request = (IntegrationId, "Release", cancellationToken);
            return new() { Snapshot = await ReadAsync(cancellationToken), Values = new() { ["ConnectionString"] = connection ?? throw new InvalidOperationException("The deployment credential is unavailable.") } };
        }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
        {
            Request = (IntegrationId, field, cancellationToken);
            return Task.FromResult(connection ?? throw new InvalidOperationException("The deployment credential is unavailable."));
        }
    }
}
