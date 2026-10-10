using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Microsoft.Aspire.Candidates;
using DigitalBrain.Microsoft.Aspire.Cluster;
using DigitalBrain.Microsoft.Aspire.Verification;

namespace DigitalBrain.Microsoft.Aspire.Tests;

// What the brain stands on: the aspire module's rows are the cluster's silos.
public sealed class InfrastructureFacts
{
    [Fact]
    public void TheAspireModuleShowsEachSiloAndOtherModulesShowNothing()
    {
        var rows = InfrastructureView.Join(["postgres", "aspire", "clickhouse"],
        [
            new("silo-1", "sha0001", "Running", "Healthy"),
            new("silo-0", "sha0001", "Exited", null),
        ]);

        Assert.Equal(
        [
            ("aspire", "silo-0", "Exited", null),
            ("aspire", "silo-1", "Running", "Healthy"),
        ], rows.Select(row => (row.Module, row.Resource, row.State, row.Health)));
    }

    [Fact]
    public void WithoutSilosTheAppHostIsUnknown()
        => Assert.Equal(("aspire", "Unknown", null),
            InfrastructureView.Join(["aspire"], []).Select(row => (row.Module, row.State, row.Health)).Single());

    [Fact]
    public async Task TheBrainRouteAnswersTheClusterSilos()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create()
            .WithModule<AspireModule>()
            .ConfigureSilo(silo =>
            {
                silo.Services.AddSingleton<ICandidateHost>(new FakeCandidateHost());
                silo.Services.AddSingleton<IClusterHost>(new FakeClusterHost());
                silo.Services.AddSingleton<ICandidateVerifier>(new FakeVerifier());
            })
            .WithHttpEdge()
            .StartAsync(ct);

        var rows = await brain.HttpClient.GetFromJsonAsync<JsonElement>("/brains/ws-infra/infrastructure", ct);

        Assert.Equal(["silo-0", "silo-1"], rows.EnumerateArray().Select(row => row.GetProperty("resource").GetString()));
    }
}
