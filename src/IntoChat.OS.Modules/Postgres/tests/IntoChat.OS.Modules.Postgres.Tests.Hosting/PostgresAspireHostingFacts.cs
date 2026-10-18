using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Postgres;
using IntoChat.OS.Postgres.Aspire.Hosting;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.Postgres.Tests.Hosting;

public sealed class PostgresAspireHostingFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedDatabaseIsProjectedWithTheConfiguredConnectionNameAndReadiness(bool persistent)
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent).Settings);
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
        Assert.Contains(IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString")), environment.Keys);
        Assert.Equal("reporting", Assert.IsType<string>(environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ModuleOptionsSection("postgres"), "ConnectionName")]));
    }

    [Fact]
    public async Task ExternalConnectionIsProjectedAsASecretWithoutProvisioningAServer()
    {
        var builder = CreateBuilder();
        builder.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=external;Database=analytics;Username=reader;Password=test-only";
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false })
            .WithHosting<PostgresModuleHosting, PostgresModuleOptions>(module => module.WithPostgres().WithConnection("reporting"));
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        var connection = Assert.IsType<ParameterResource>(environment[IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString"))]);
        Assert.True(connection.Secret);
        Assert.Equal(builder.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")], await connection.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ModuleConfigurationCreatesOneDatabaseForMultipleConsumers()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false })
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
            Assert.Contains(IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString")), (await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken)).Keys);
        }
    }

    [Fact]
    public void RepeatedHostingConfigurationDoesNotCreateDuplicateResources()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Settings);
        var hosting = new PostgresModuleHosting();
        hosting.Configure(brain);
        builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
        brain.GetModuleConfiguration("postgres")[IntoChatOSConfiguration.ModuleOptionsSection("postgres") + ":Hosting:DatabaseName"] = "different";
        Assert.Throws<InvalidOperationException>(() => hosting.Configure(brain));
        Assert.Single(builder.Resources.OfType<PostgresDatabaseResource>());
    }

    [Fact]
    public void A_hosting_adapter_claims_its_module_through_the_generic_parameter()
        => Assert.Equal("postgres", new PostgresModuleHosting().Module.ToString());

    [Fact]
    public async Task HostedPostgresInRunModeSelectsDatabasePerBrainWithTheAdminConnection()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Settings);
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Contains(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "AdminConnection")), environment.Keys);
        Assert.Equal("DatabasePerBrain", environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ModuleOptionsSection("postgres"), "Isolation")]);
    }

    [Fact]
    public async Task HostedPostgresInPublishModeEmitsTheSecretConnectionParameterInstead()
    {
        var builder = CreatePublishBuilder();
        builder.Configuration["Parameters:postgres-connection"] = "Host=azure;Database=intochat_os;Username=app;Password=test-only";
        var brain = builder.AddIntoChatOS("brain", options: new() { PersistentStorage = false });
        brain.WithHosting(new PostgresModuleHosting(), Definition(enabled: true, persistent: false).Settings);
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);

        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        var connection = Assert.IsType<ParameterResource>(environment[IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "ConnectionString"))]);
        Assert.True(connection.Secret);
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("postgres", "AdminConnection")), environment.Keys);
    }

    private static ModuleDefinition Definition(bool enabled, bool persistent)
        => ModuleOptionsSerialization.Compile<PostgresModule, PostgresModuleOptions>(new()
        {
            ConnectionName = "reporting",
            Hosting = new() { Enabled = enabled, DatabaseName = "analytics", PersistentStorage = persistent },
        });
}
