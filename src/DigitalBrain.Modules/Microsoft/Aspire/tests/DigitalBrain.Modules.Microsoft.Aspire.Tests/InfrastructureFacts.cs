using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Microsoft.Aspire;

namespace DigitalBrain.Modules.Microsoft.Aspire.Tests;

// What the brain stands on: each composed module with the AppHost resource that carries its id.
public sealed class InfrastructureFacts
{
    [Fact]
    public void ModulesJoinTheResourcesThatCarryTheirIdAndAspireIsTheAppHost()
    {
        var rows = InfrastructureView.Join(["postgres", "aspire", "clickhouse", "ai"],
        [
            new("Modules-postgres-server", "Container", "Running", "Healthy", []),
            new("Modules-postgres-database", "Database", "Running", null, []),
            new("Modules-clickhouse", "Container", "Exited", "Unhealthy", []),
            new("OperatingSystem", "Project", "Running", "Healthy", []),
        ]);

        Assert.Equal(
        [
            ("aspire", "apphost", "Running", "Healthy"),
            ("clickhouse", "Modules-clickhouse", "Exited", "Unhealthy"),
            ("postgres", "Modules-postgres-database", "Running", null),
            ("postgres", "Modules-postgres-server", "Running", "Healthy"),
        ], rows.Select(row => (row.Module, row.Resource, row.State, row.Health)));
    }

    [Fact]
    public void WithoutAnyReportTheAppHostIsUnknown()
        => Assert.Equal(("aspire", "Unknown", null),
            InfrastructureView.Join(["aspire"], []).Select(row => (row.Module, row.State, row.Health)).Single());

    [Fact]
    public async Task TheBrainRouteAnswersTheJoinedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AspireModule>()
            .ConfigureModule<AspireModule, AspireOptions>(aspire => aspire.ApplicationName = "DigitalBrain")
            .WithHttpEdge()
            .StartAsync(ct);
        await brain.Grains.GetGrain<IAspireResourceReporter>("DigitalBrain")
            .Report(new("Modules-aspire-dashboard", "Container", "Running", "Healthy", []));

        var rows = await brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/ws-infra/infrastructure", ct);

        var aspire = Assert.Single(rows.EnumerateArray());
        Assert.Equal(("aspire", "apphost", "Running", "Healthy"),
            (aspire.GetProperty("module").GetString(), aspire.GetProperty("resource").GetString(),
             aspire.GetProperty("state").GetString(), aspire.GetProperty("health").GetString()));
    }
}
