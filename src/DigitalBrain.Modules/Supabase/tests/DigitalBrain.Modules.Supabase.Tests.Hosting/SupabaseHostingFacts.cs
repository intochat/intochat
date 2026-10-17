using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Aspire.Hosting;
using static DigitalBrain.Aspire.Testing.AspireCompositionTest;

namespace DigitalBrain.Modules.Supabase.Tests.Hosting;

public sealed class SupabaseHostingFacts
{
    [Fact]
    public async Task UserOnlyModuleAddsNoDeploymentDatabaseOrCredentialParameter()
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { PersistentStorage = false })
            .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(_ => { });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource => resource.Name.Contains("supabase", StringComparison.Ordinal));
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), environment.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredBackingStoreProjectsItsSelectionAndIntegrationSeed(bool hosted)
    {
        var builder = CreateBuilder();
        builder.Configuration["Parameters:supabase-connection"] = "Host=external;Database=infra;Username=reader";
        var brain = builder.AddDigitalBrain("brain", options: new() { PersistentStorage = false })
            .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(options =>
            {
                if (hosted) { options.WithPostgres(); }
                else { options.WithConnection("infra"); }
            });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("true", environment[DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore))]);
        Assert.Contains(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), environment.Keys);
        Assert.Equal(hosted, builder.Resources.OfType<PostgresServerResource>().Any());
    }
}
