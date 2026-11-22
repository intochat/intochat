using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.CustomerResearcher;
using IntoChat.OS.Modules.Postgres.Tables;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Tests.Integration.Apps;

public sealed class CustomerResearcherAppContractFacts(FlagshipAppsRuntimeFixture fixture)
    : AppConcurrencyAndIsolationContract<CustomerResearcherApp, FlagshipAppsRuntimeFixture>(fixture)
{
    protected override string? ConcurrentActivationKey => "btn:clear";

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ResearchAndClear_PersistsProfilesAndClearsCleanly_ViaAppSession(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await Open(kind, ct: ct);

        app.ShouldDisplayText("Customer Researcher");
        var initialGen = app.CurrentView.Generation;

        await app.Send(new ResearchCompany("Acme Corp", "SaaS", "Enterprise workflow automation", 92), ct);
        Assert.True(app.CurrentView.Generation > initialGen, "Expected generation to increment after researching Acme Corp.");
        app.ShouldDisplayText("Acme Corp")
            .ShouldDisplayText("SaaS")
            .ShouldDisplayText("92");

        var afterFirstGen = app.CurrentView.Generation;
        await app.Send(new ResearchCompany("Globex", "AI", "Autonomous agents", 88), ct);
        Assert.True(app.CurrentView.Generation > afterFirstGen, "Expected generation to increment after researching Globex.");
        app.ShouldDisplayText("Acme Corp")
            .ShouldDisplayText("Globex")
            .ShouldDisplayText("AI")
            .ShouldDisplayText("88");

        await app.Click("btn:clear", ct);
        app.ShouldDisplayText("No companies researched yet");
    }

    [Fact]
    public async Task ResearchAndClear_PersistsAndDeletesRows_WhenPostgresTableIsAttached()
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new PostgresTableSpyRecorder();
        var services = new ServiceCollection().AddSingleton(recorder).BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services);
        os.Register<CustomerResearcherApp>();
        os.Register<SpyPostgresTableNeuron>();

        await using var app = await AppSession.OpenAsync<CustomerResearcherApp>(os, ct: ct);

        await app.Send(new ResearchCompany("Acme Corp", "SaaS", "Enterprise workflow automation", 92), ct);

        Assert.Single(recorder.Definitions);
        Assert.Equal(["company_name"], recorder.Definitions[0].PrimaryKey);
        Assert.Contains(recorder.Definitions[0].Columns, c => c.Name == "company_name" && c.Type == "text");
        Assert.Contains(recorder.Definitions[0].Columns, c => c.Name == "industry" && c.Type == "text");
        Assert.Contains(recorder.Definitions[0].Columns, c => c.Name == "summary" && c.Type == "text");
        Assert.Contains(recorder.Definitions[0].Columns, c => c.Name == "fit_score" && c.Type == "number");
        Assert.Contains(recorder.Definitions[0].Columns, c => c.Name == "status" && c.Type == "text");

        Assert.Single(recorder.Upserts);
        var (key, values) = recorder.Upserts[0];
        Assert.Equal("company_name", key[0].Column);
        Assert.Equal("\"Acme Corp\"", key[0].Json);
        Assert.Contains(values, v => v.Column == "industry" && v.Json == "\"SaaS\"");
        Assert.Contains(values, v => v.Column == "summary" && v.Json == "\"Enterprise workflow automation\"");
        Assert.Contains(values, v => v.Column == "fit_score" && v.Json == "92");
        Assert.Contains(values, v => v.Column == "status" && v.Json == "\"High Fit\"");

        await app.Send(new ClearResearch(), ct);

        Assert.Single(recorder.Deletes);
        Assert.Equal("company_name", recorder.Deletes[0][0].Column);
        Assert.Equal("\"Acme Corp\"", recorder.Deletes[0][0].Json);
    }

    private sealed class PostgresTableSpyRecorder
    {
        public List<TableDefinition> Definitions { get; } = [];
        public List<(TableValue[] Key, TableValue[] Values)> Upserts { get; } = [];
        public List<TableValue[]> Deletes { get; } = [];
    }

    [Neuron("postgres.table")]
    private sealed class SpyPostgresTableNeuron(PostgresTableSpyRecorder recorder) : Neuron, ITable
    {
        public Task<AcceptedTable> Define(TableDefinition definition)
        {
            recorder.Definitions.Add(definition);
            return Task.FromResult(new AcceptedTable(Id.Key, definition, 1));
        }

        public Task<bool> Upsert(TableValue[] key, TableValue[] values)
        {
            recorder.Upserts.Add((key, values));
            return Task.FromResult(true);
        }

        public Task<bool> Delete(TableValue[] key)
        {
            recorder.Deletes.Add(key);
            return Task.FromResult(true);
        }

        public Task<TableValue[]?> Read(TableValue[] key) => Task.FromResult<TableValue[]?>(null);

        public Task<TableValue[][]> Page(int offset = 0, int limit = 200) => Task.FromResult<TableValue[][]>([]);
    }
}
