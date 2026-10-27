using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Modules.Supabase.Aspire.Hosting;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.Supabase.Tests.Hosting;

public sealed class SupabaseHostingFacts
{
    [Fact]
    public async Task UserOnlyModuleAddsNoDeploymentDatabaseOrCredentialParameter()
    {
        var builder = CreateBuilder();
        var brain = builder.AddIntoChatOS("brain", options: new())
            .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(_ => { });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        Assert.Empty(builder.Resources.OfType<PostgresServerResource>());
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), resource => resource.Name.Contains("supabase", StringComparison.Ordinal));
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), environment.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredBackingStoreProjectsItsSelectionAndIntegrationSeed(bool hosted)
    {
        var builder = CreateBuilder();
        builder.Configuration["Parameters:supabase-connection"] = "Host=external;Database=infra;Username=reader";
        var brain = builder.AddIntoChatOS("brain", options: new())
            .WithHosting<SupabaseModuleHosting, SupabaseModuleOptions>(options =>
            {
                if (hosted) { options.WithPostgres(); }
                else { options.WithConnection("infra"); }
            });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Equal("true", environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ModuleOptionsSection("supabase"), nameof(SupabaseModuleOptions.UseBackingStore))]);
        Assert.Contains(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("supabase", "ConnectionString")), environment.Keys);
        Assert.Equal(hosted, builder.Resources.OfType<PostgresServerResource>().Any());
    }
}
