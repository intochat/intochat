using DigitalBrain.Framework.Capacity;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Modules.Postgres.Tests;

public sealed class PostgresCapacityFacts
{
    [Fact]
    public async Task AComposedPostgresModuleResolvesThePlatformOriginForTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PostgresModule>()
            .ConfigureSilo(silo => silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=sample;Username=reader")
            .StartAsync(ct);
        var capacity = brain.SiloServices.GetRequiredService<ICapacity>();
        var resolved = await capacity.Resolve("postgres", new("brain-1", "app-1"), ct);
        Assert.Equal("platform", resolved.Origin);
    }

    [Fact]
    public async Task ProvisioningIsRefusedWhenOnlyThePlatformConnectionExists()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PostgresModule>()
            .ConfigureSilo(silo => silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=sample;Username=reader")
            .StartAsync(ct);
        var capacity = brain.SiloServices.GetRequiredService<ICapacity>();
        var refusal = await Assert.ThrowsAsync<ServiceUnavailableException>(
            async () => await capacity.Provision("postgres", new("brain-1", "app-1"), ct));
        Assert.Equal("Capacity is not provisioned.", refusal.Message);
    }

    [Fact]
    public void DatabaseNamesAreStableHashedAndDistinctPerBrain()
    {
        var one = DockerPostgresProvisioner.DatabaseName("brain-1");
        var two = DockerPostgresProvisioner.DatabaseName("brain-2");
        Assert.Equal(one, DockerPostgresProvisioner.DatabaseName("brain-1"));
        Assert.NotEqual(one, two);
        Assert.Matches("^brain_[0-9a-f]{16}$", one);
    }

    [Fact]
    public async Task AnAdminConnectionActivatesTheProvisionerAndDeactivatesThePlatformSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<PostgresModule>()
            .ConfigureSilo(silo =>
            {
                silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=sample;Username=reader";
                silo.Configuration[DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.ModuleOptionsSection("postgres") + ":ProvisionPerBrain"] = "true";
                silo.Configuration[IntegrationSchema.Key("postgres", "AdminConnection")] = "Host=localhost;Database=postgres;Username=admin;Password=admin";
                silo.Services.AddSingleton<ICapacityProvisioner>(new DockerPostgresProvisioner(
                    "Host=localhost;Database=postgres;Username=admin;Password=admin", (_, _, _) => Task.CompletedTask));
            }).StartAsync(ct);
        var capacity = brain.SiloServices.GetRequiredService<ICapacity>();
        var resolved = await capacity.Resolve("postgres", new("brain-1", "app-1"), ct);
        Assert.Equal("db:" + DockerPostgresProvisioner.DatabaseName("brain-1"), resolved.Origin);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "Password=invalid-unused-admin-canary")]
    [InlineData(true, "Host=localhost;Database=postgres;Username=admin")]
    public async Task StartupRequiresAdminOnlyWhenPerBrainProvisioningIsSelected(bool provisionPerBrain, string? admin)
    {
        var bindings = new StartupBindings(admin);
        await using var sources = new PostgresSourceRegistry(bindings, Options.Create(new PostgresModuleOptions { ProvisionPerBrain = provisionPerBrain }));
        await sources.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, bindings.Releases);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Password=invalid-admin-canary")]
    public async Task SelectedProvisioningRejectsMissingOrInvalidAdminBeforeStartupCompletes(string? admin)
    {
        var bindings = new StartupBindings(admin);
        await using var sources = new PostgresSourceRegistry(bindings, Options.Create(new PostgresModuleOptions { ProvisionPerBrain = true }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sources.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, bindings.Releases);
        Assert.DoesNotContain("invalid-admin-canary", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class StartupBindings(string? admin) : IModuleIntegrations<PostgresModule>, IIntegrationHandle
    {
        public string IntegrationId => "postgres";
        public int Releases { get; private set; }
        public IIntegrationHandle Get(string integrationId) { Assert.Equal(IntegrationId, integrationId); return this; }
        public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Releases++;
            var values = new Dictionary<string, string> { ["ConnectionString"] = "Host=localhost;Database=platform;Username=reader" };
            if (admin is not null) { values["AdminConnection"] = admin; }
            return new() { Snapshot = await ReadAsync(cancellationToken), Values = values };
        }
        public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new IntegrationSnapshot { IntegrationId = IntegrationId, Status = IntegrationStatus.Ready });
        public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public void TheRegistryAnswersThePlatformSourceForANullOrLegacyOrigin()
    {
        Assert.Equal(PostgresCapacityKind.PlatformOrigin, PostgresCapacityKind.OriginOrPlatform(null));
        Assert.Equal("db:brain_x", PostgresCapacityKind.OriginOrPlatform("db:brain_x"));
    }
}
