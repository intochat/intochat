using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Windows;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Windows;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DigitalBrain.Modules.Postgres.Windows.Tests;

public sealed class PostgresSupabaseCompositionFacts
{
    [Fact]
    public async Task PostgresWindowsKeepsItsSourceKeyedBesideSupabasesDefaultSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<SupabaseModule>().WithModule<PostgresModule>().WithModule<PostgresWindowsModule>()
            .ConfigureSilo(silo =>
            {
                silo.Configuration[IntegrationSchema.Key("postgres", "ConnectionString")] = "Host=localhost;Database=analytics;Username=reader";
                silo.Configuration[IntegrationSchema.Key("supabase", "ConnectionString")] = "Host=localhost;Database=supabase;Username=reader";
            }).StartAsync(ct);
        var services = brain.SiloServices;
        Assert.Single(services.GetServices<IPostgresProvider>());
        Assert.Null(services.GetService<NpgsqlDataSource>());
        Assert.Same(services.GetRequiredService<ISupabaseProvider>(), services.GetRequiredService<ILiveTableSource>());
        Assert.IsType<PostgresIntegrationLiveSource>(services.GetRequiredKeyedService<ILiveTableSource>("postgres"));
    }
}
