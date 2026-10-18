using System.Net;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;
using DigitalBrain.Microsoft.CSharp.Sessions;
using DigitalBrain.Postgres;
using DigitalBrain.Postgres.Tables;
using DigitalBrain.Postgres.Tables.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

// A subscription speaks for its behavior as the behavior's calls do: the source sees the program's principal
// stamped as its app, so its access policy and its own guards apply to a watch as they apply to a call. A
// source that refuses a program feeds it nothing: the refused subscription is not kept, and one the source
// stops accepting is buffered neither live nor from the source's log. The sandbox's reports reach the edge as
// the Open posture's owner, as they do on a live brain.
public sealed class SubscriptionCallerFacts
{
    private static readonly TableDefinition Definition = new([new("id", "text"), new("value", "text")], ["id"]);

    [Trait("host", "own")]
    [Fact]
    public async Task ABehaviorSubscribesToItsOwnAppsTableAndReceivesItsRowsAgainAfterAResubscribe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartWithTablesAsync(ct);
        var owner = brain.Owner;
        var app = owner.Key("apps/rows");
        var table = brain.Get<ITable>(app + "/items");
        using (owner.AsApp(app)) { await table.Define(Definition); }
        var session = await AssignAsync(brain, owner, app, ct);
        var edge = RuntimeBrain.Edge(brain, owner.BrainId);
        var subscribe = Subscribe<ITable>(app, app + "/items", nameof(RowUpserted));

        await SubscribeAsync(brain, owner.BrainId, session, subscribe, ct);
        await UpsertAsync(brain, owner, app, table, "one", ct);
        var first = Assert.Single(await edge.PendingBatches(session.Id));
        Assert.Equal("one", RowId(Assert.Single(first.Signals)));

        // A table that lost its activation lost its watchers; the behavior's reload subscribes again.
        await brain.DeactivateAsync(table, ct);
        await SubscribeAsync(brain, owner.BrainId, session, subscribe, ct);
        await UpsertAsync(brain, owner, app, table, "two", ct);
        var pending = Assert.Single(await edge.PendingBatches(session.Id));
        Assert.Equal(["one", "two"], pending.Signals.Select(RowId));
    }

    // One watch serves every program on a source, so the other app's refused subscription must not survive to
    // be fed by the rows the table's own app is watching.
    [Trait("host", "own")]
    [Fact]
    public async Task AnotherAppsTableRefusesASubscriptionAndKeepsNoneForItsRowsToFeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartWithTablesAsync(ct);
        var owner = brain.Owner;
        var app = owner.Key("apps/rows");
        var other = owner.Key("apps/other");
        var table = brain.Get<ITable>(app + "/items");
        using (owner.AsApp(app)) { await table.Define(Definition); }
        var session = await AssignAsync(brain, owner, app, ct);
        using (owner.AsCaller()) { await RuntimeBrain.Programs(brain, owner.BrainId).Assign(other, RuntimeBrain.One(RuntimeBrain.Idle), []); }
        await SubscribeAsync(brain, owner.BrainId, session, Subscribe<ITable>(app, app + "/items", nameof(RowUpserted)), ct);

        using var refused = await Report(brain, owner.BrainId, session, Subscribe<ITable>(other, app + "/items", nameof(RowUpserted)), ct);
        await UpsertAsync(brain, owner, app, table, "one", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("another brain or app", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(await SubscriptionsOf(brain, owner.BrainId, other, ct));
        var batch = Assert.Single(await RuntimeBrain.Edge(brain, owner.BrainId).PendingBatches(session.Id));
        Assert.Equal("one", RowId(Assert.Single(batch.Signals)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABehaviorCannotSubscribeToANeuronOfAnotherBrainWhicheverTurnWatchesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var bob = await brain.PersonAsync("bob", ct);
        var app = bob.Key("apps/spy");
        var session = await AssignAsync(brain, bob, app, ct);
        var theirs = BrainRunsKeys.For(brain.Owner.BrainId);

        using var refused = await Report(brain, bob.BrainId, session, Subscribe<IBrainRuns>(app, theirs, nameof(BehaviorRan)), ct);
        var kept = await SubscriptionsOf(brain, bob.BrainId, app, ct);
        // Renewals and reminders watch on turns that carry no caller; the source still sees the behavior.
        var unstamped = await Assert.ThrowsAsync<UnauthorizedAccessException>(()
            => RuntimeBrain.Edge(brain, bob.BrainId).Subscribed(session.Id, app, brain.Get<IBrainRuns>(theirs).GetGrainId().ToString(), nameof(BehaviorRan)));
        await brain.DeactivateAsync(RuntimeBrain.Runtime(brain, bob.BrainId), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("operation Watch", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Contains("operation Watch", unstamped.Message, StringComparison.Ordinal);
        Assert.Empty(kept);
        Assert.Empty(await SubscriptionsOf(brain, bob.BrainId, app, ct));
    }

    // A program the platform assigned has no principal, so its watches pass unstamped, as grain code's do;
    // reassigned by bob it speaks for bob, and the reassignment's own watch finds the other brain's app refusing
    // it. That subscription stays, unfed: the watch it held before the refusal outlives the refusal, and the
    // app's log is still there to be read. Only the behavior subscribing again, refused, lets it go.
    [Trait("host", "own")]
    [Fact]
    public async Task ASourceThatStopsAcceptingAProgramFeedsItNothingLiveOrFromItsLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct, apps: true);
        var bob = await brain.PersonAsync("bob", ct);
        var theirs = brain.Get<IApp>(IApp.Key(brain.Owner.BrainId, "echo"));
        await theirs.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        var mine = brain.Get<IPinger>(bob.Key("apps/source/pinger"));
        var app = bob.Key("apps/spy");
        var runtime = RuntimeBrain.Runtime(brain, bob.BrainId);
        var edge = RuntimeBrain.Edge(brain, bob.BrainId);
        await RuntimeBrain.Programs(brain, bob.BrainId).Assign(app, RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await runtime.Inspect(ct)).SessionId!;
        await edge.Subscribed(session, app, theirs.GetGrainId().ToString(), nameof(AppRequested));
        await edge.Subscribed(session, app, mine.GetGrainId().ToString(), nameof(Pinged));
        using (bob.AsCaller()) { await RuntimeBrain.Programs(brain, bob.BrainId).Assign(app, RuntimeBrain.One(RuntimeBrain.Idle), []); }
        // Reassignment renews watches off-turn. The accepted sentinel must be ready under its new caller.
        await edge.Subscribed(session, app, mine.GetGrainId().ToString(), nameof(Pinged));

        // Each arrival is the runtime's next turn after its signal, in order: bob's own ping is the sentinel.
        await using (var changes = await brain.Observe<BrainRuntimeChanged>(runtime, ct))
        {
            await theirs.Request("open", "after the refusal");
            await mine.Ping(1);
            await changes.NextAsync(ct: ct);
        }
        var live = await edge.PendingBatches(session);
        await brain.DeactivateAsync(runtime, ct);
        await using var resumed = await brain.Observe<BrainRuntimeChanged>(runtime, ct);
        // Observing the runtime readies only this probe; a relaunched behavior subscribes before publishing.
        await edge.Subscribed((await runtime.Inspect(ct)).SessionId!, app, mine.GetGrainId().ToString(), nameof(Pinged));
        await theirs.Request("open", "while the runtime slept");
        await mine.Ping(2);
        IReadOnlyList<SignalBatchEvent> pending;
        do { await resumed.NextAsync(ct: ct); pending = await edge.PendingBatches((await runtime.Inspect(ct)).SessionId!); }
        while (!pending.SelectMany(batch => batch.Signals).Select(Ping).Contains(2));
        var unfed = await SubscriptionsOf(brain, bob.BrainId, app, ct);
        var relaunched = (await runtime.Inspect(ct)).SessionId!;
        var resubscribed = await Assert.ThrowsAsync<UnauthorizedAccessException>(()
            => edge.Subscribed(relaunched, app, theirs.GetGrainId().ToString(), nameof(AppRequested)));

        Assert.Equal<int?>([1], live.SelectMany(batch => batch.Signals).Select(Ping));
        Assert.Equal<int?>([1, 2], pending.SelectMany(batch => batch.Signals).Select(Ping));
        Assert.Equal([nameof(AppRequested), nameof(Pinged)], unfed.Select(view => view.Signal).Order(StringComparer.Ordinal));
        Assert.Contains("operation Watch", resubscribed.Message, StringComparison.Ordinal);
        Assert.Equal([nameof(Pinged)], (await SubscriptionsOf(brain, bob.BrainId, app, ct)).Select(view => view.Signal));
    }

    // Unwatch is judged as Watch is: a table refuses any caller but its own app's.
    [Trait("host", "own")]
    [Fact]
    public async Task RetiringAProgramUnwatchesItsSourceAsTheProgram()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.StartAsync(new FakeSessionHost(), ct);
        var owner = brain.Owner;
        var app = owner.Key("apps/pings");
        var source = owner.Key("apps/source/pinger");
        var pinger = brain.Get<IPinger>(source);
        var session = await AssignAsync(brain, owner, app, ct);
        await SubscribeAsync(brain, owner.BrainId, session, Subscribe<IPinger>(app, source, nameof(Pinged)), ct);

        using (owner.AsCaller()) { await RuntimeBrain.Programs(brain, owner.BrainId).Retire(app); }

        Assert.Equal($"{owner.PrincipalId} App AppProxy {app}", await pinger.Unwatched());
    }

    private static Task<ITestBrain> StartWithTablesAsync(CancellationToken ct)
        => RuntimeBrain.Create(new FakeSessionHost()).WithModule<PostgresModule>()
            .WithIntegration(PostgresModule.Integration, "ConnectionString", "Host=localhost;Database=sample;Username=reader")
            // The store stands in for the database: the table's guards, not its rows, are under test.
            .ConfigureServices(services => services.AddSingleton<ITableStore>(new MemoryTables()))
            .StartAsync(ct);

    // The program is assigned by its person, as an install is, so the runtime records that principal for it.
    private static async Task<(string Id, string Token)> AssignAsync(ITestBrain brain, Person person, string app, CancellationToken ct)
    {
        using (person.AsCaller()) { await RuntimeBrain.Programs(brain, person.BrainId).Assign(app, RuntimeBrain.One(RuntimeBrain.Idle), []); }
        var id = (await RuntimeBrain.Runtime(brain, person.BrainId).Inspect(ct)).SessionId!;
        return (id, brain.Services.GetRequiredService<SessionTokens>().Issue(person.BrainId, id));
    }

    private static RuntimeReport Subscribe<TContract>(string behavior, string neuron, string signal)
        => new(Subscribe: new(behavior, typeof(TContract).FullName!, neuron, signal));

    private static async Task<CSharpSubscriptionView[]> SubscriptionsOf(ITestBrain brain, string scope, string behavior, CancellationToken ct)
        => (await RuntimeBrain.Runtime(brain, scope).Inspect(ct)).Behaviors.Single(view => view.Id == behavior).Subscriptions;

    private static async Task SubscribeAsync(ITestBrain brain, string scope, (string Id, string Token) session, RuntimeReport subscribe, CancellationToken ct)
    {
        using var subscribed = await Report(brain, scope, session, subscribe, ct);
        Assert.True(subscribed.StatusCode == HttpStatusCode.OK, await subscribed.Content.ReadAsStringAsync(ct));
    }

    // The arrival is the runtime's next change after the app's write.
    private static async Task UpsertAsync(ITestBrain brain, Person owner, string app, ITable table, string id, CancellationToken ct)
    {
        await using var changes = await brain.Observe<BrainRuntimeChanged>(RuntimeBrain.Runtime(brain, owner.BrainId), ct);
        using (owner.AsApp(app)) { await table.Upsert(Row(id), [new("value", JsonSerializer.Serialize(id))]); }
        await changes.NextAsync(ct: ct);
    }

    private static Task<HttpResponseMessage> Report(ITestBrain brain, string scope, (string Id, string Token) session, RuntimeReport report, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/brains/" + scope + "/" + RuntimeProtocol.Report)
        { Content = JsonContent.Create(report, options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", session.Token);
        return brain.HttpClient.SendAsync(request, ct);
    }

    // A ping's number; null for any other signal, such as the refusing app's requests.
    private static int? Ping(string json) => JsonDocument.Parse(json).RootElement.TryGetProperty("number", out var number) ? number.GetInt32() : null;

    private static TableValue[] Row(string id) => [new("id", JsonSerializer.Serialize(id))];
    private static string RowId(string json)
        => JsonSerializer.Deserialize<string>(JsonSerializer.Deserialize<RowUpserted>(json, RuntimeProtocol.Json)!.Key.Single(value => value.Column == "id").Json)!;

    private sealed class MemoryTables : ITableStore
    {
        private readonly Dictionary<string, Dictionary<string, TableValue[]>> _tables = new(StringComparer.Ordinal);
        private static string RowKey(TableValue[] key) => JsonSerializer.Serialize(key);
        public Task DefineAsync(TablePlacement placement, string table, TableDefinition definition, CancellationToken ct)
        { _tables.TryAdd(table, new(StringComparer.Ordinal)); return Task.CompletedTask; }
        public Task DropAsync(TablePlacement placement, string table, CancellationToken ct) { _tables.Remove(table); return Task.CompletedTask; }
        public Task<bool> UpsertAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, TableValue[] values, CancellationToken ct)
        { _tables[table][RowKey(key)] = [.. key, .. values]; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
            => Task.FromResult(_tables[table].Remove(RowKey(key)));
        public Task<TableValue[]?> ReadAsync(TablePlacement placement, string table, TableDefinition definition, TableValue[] key, CancellationToken ct)
            => Task.FromResult(_tables[table].GetValueOrDefault(RowKey(key)));
        public Task<TableValue[][]> PageAsync(TablePlacement placement, string table, TableDefinition definition, int offset, int limit, CancellationToken ct)
            => Task.FromResult(_tables[table].Values.Skip(offset).Take(limit).ToArray());
    }
}
