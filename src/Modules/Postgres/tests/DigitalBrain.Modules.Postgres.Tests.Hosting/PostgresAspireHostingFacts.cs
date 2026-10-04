using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Aspire.Hosting;
using static DigitalBrain.Aspire.Testing.AspireCompositionTest;

namespace DigitalBrain.Modules.Postgres.Tests.Hosting;

public sealed class PostgresAspireHostingFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedDatabaseIsProjectedWithTheConfiguredConnectionNameAndReadiness(bool persistent)
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent).Configuration);
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        var server = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        var database = Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
        Assert.Same(server, database.Parent);
        Assert.Equal("analytics", database.DatabaseName);
        Assert.Contains(consumer.Resource.Annotations.OfType<WaitAnnotation>(), wait =>
            ReferenceEquals(wait.Resource, database) && wait.WaitType == WaitType.WaitUntilHealthy);
        Assert.Equal(persistent, server.Annotations.OfType<ContainerMountAnnotation>().Any(mount => mount.Type == ContainerMountType.Volume));
        Assert.Equal(persistent, server.Annotations.OfType<ContainerLifetimeAnnotation>().Any(lifetime => lifetime.Lifetime == ContainerLifetime.Persistent));
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Contains("ConnectionStrings__reporting", environment.Keys);
        Assert.Equal("reporting", Assert.IsType<string>(environment[DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("postgres"), "ConnectionName")]));
    }

    [Fact]
    public async Task ExternalConnectionIsProjectedAsASecretWithoutProvisioningAServer()
    {
        var builder = CreateBuilder();
        builder.Configuration["ConnectionStrings:reporting"] = "Host=external;Database=analytics;Username=reader;Password=test-only";
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false })
            .WithHosting<PostgresModuleHosting, PostgresModuleOptions>(module => module.WithPostgres().WithConnection("reporting"));
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        var connection = Assert.IsType<ParameterResource>(environment["ConnectionStrings__reporting"]);
        Assert.True(connection.Secret);
        Assert.Equal(builder.Configuration["ConnectionStrings:reporting"], await connection.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModuleConfigurationCreatesOneDatabaseForMultipleConsumers()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false })
            .WithHosting<PostgresModuleHosting, PostgresModuleOptions>(module => module.WithConnection("reporting").WithPostgres(options =>
            {
                options.DatabaseName = "analytics";
                options.PersistentStorage = false;
            }));
        var first = builder.AddExecutable("first", "unused", ".").WithReference(brain);
        var second = builder.AddExecutable("second", "unused", ".").WithReference(brain);
        var database = Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
        Assert.Equal("analytics", database.DatabaseName);
        Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        foreach (var consumer in new[] { first, second })
        {
            Assert.Contains(consumer.Resource.Annotations.OfType<WaitAnnotation>(), wait => ReferenceEquals(wait.Resource, database));
            Assert.Contains("ConnectionStrings__reporting", (await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken)).Keys);
        }
    }

    [Fact]
    public void RepeatedHostingConfigurationDoesNotCreateDuplicateResources()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Configuration);
        var hosting = new PostgresModuleHosting();
        hosting.Configure(brain);
        builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
        brain.GetModuleConfiguration("postgres")[DigitalBrainConfiguration.ModuleOptionsSection("postgres") + ":Hosting:DatabaseName"] = "different";
        Assert.Throws<InvalidOperationException>(() => hosting.Configure(brain));
        Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
    }

    [Fact]
    public void AModuleTypeResolvesItsHostingAdapterFromTheAttribute()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false });
        brain.WithModule(typeof(PostgresModule), Definition(enabled: true, persistent: false).Configuration);
        Assert.Single(builder.Resources.OfType<PostgresServerResource>());
    }

    [Fact]
    public async Task HostedPostgresInRunModePassesTheAdminConnectionToTheBrain()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Configuration);
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Contains(DigitalBrainConfiguration.EnvironmentName(PostgresModuleOptions.AdminConnectionKey), environment.Keys);
    }

    [Fact]
    public async Task HostedPostgresInPublishModeEmitsTheSecretConnectionParameterInstead()
    {
        var builder = CreatePublishBuilder();
        builder.Configuration["Parameters:postgres-connection"] = "Host=azure;Database=digitalbrain;Username=app;Password=test-only";
        var brain = builder.AddDigitalBrain("brain", options: new() { UseAzureStorage = true, PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Configuration);
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        var connection = Assert.IsType<ParameterResource>(environment["ConnectionStrings__reporting"]);
        Assert.True(connection.Secret);
        Assert.DoesNotContain(DigitalBrainConfiguration.EnvironmentName(PostgresModuleOptions.AdminConnectionKey), environment.Keys);
    }

    private static ModuleDefinition Definition(bool enabled, bool persistent)
        => ModuleOptionsSerialization.Compile<PostgresModule, PostgresModuleOptions>(new()
        {
            ConnectionName = "reporting",
            Hosting = new() { Enabled = enabled, DatabaseName = "analytics", PersistentStorage = persistent },
        });
}
