using IntoChat.OS.Tests;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Flutter.Fields;

namespace IntoChat.OS.Modules.Flutter.Tests;

// The plain Flutter neurons (06-04), one fact body per behaviour. The Unit tier runs each on TestOS; the Integration tier
// compiles this same file and runs each on TestOS and on SiloHost.Local, so the same fact holds on both runtimes (R4-05).
public sealed class ControlNeuronFacts(FlutterFixture fixture) : IClassFixture<FlutterFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    public static TheoryData<NeuronRuntimeKind> Kinds => FlutterFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task ASecretFieldNeverJournalsItsValue(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        var id = runtime.Id(UIVocabulary.FieldType, "apps/login/secret");
        var field = runtime.Get<IField>(id);
        // What the edge stamps on the person's request; a field refuses an unstamped call.
        using var person = runtime.Edge.Enter(runtime.Edge.Person("person", "person", runtime.Brain));

        await field.Configure(DigitalBrain.UI.FieldKind.Secret);
        await field.Set("hunter2-set");
        await field.Input("hunter2-input");

        var changes = await runtime.WaitForSignals<FieldChanged>(id, 2, Within, ct);
        Assert.All(changes, change => Assert.Equal("secret", change.Name));
        Assert.Equal("••••••••", (await field.Read()).Value);
    }
    // 06-05b: the chat index is plain; it titles a default entry from the first message and announces each change.
    [Theory, MemberData(nameof(Kinds))]
    public async Task TheChatIndexTitlesItsFirstMessageAndArchivesByIdOnly(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id(Sessions.Type, "apps/ino-data/chats");
        var index = runtime.Get<ISessionList>(id);

        await index.Create("main", Sessions.NewChat);
        await index.Touch("main", "  plan   the week  ");
        await index.Archive("main");
        await Assert.ThrowsAsync<ArgumentException>(() => index.Archive("not-an-id"));

        Assert.Equal("plan the week", (await index.Get("main"))!.Title);
        Assert.Empty((await index.List(archived: false)).Entries);
        Assert.Equal("main", Assert.Single((await index.List(archived: true)).Entries).Id);
        var changes = await runtime.WaitForSignals<SessionsChanged>(id, 3, Within, ct);
        Assert.All(changes, change => Assert.Equal($"{id.Brain.Value}/apps/ino-data/chats", change.Name));
        Assert.Equal([1L, 2L, 3L], changes.Select(change => change.Revision));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task ComputeNeuronReturnsDefaultUsageAndRecordsNewUsage(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        // The person reads only the brain's own compute neuron, keyed compute (task 7 ruling: ComputeNeuron.EnsureAuthorized).
        var id = runtime.Id("ui.compute", "compute");
        var compute = runtime.Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>(id);

        var initial = await compute.Read();
        Assert.Equal(0L, initial.SpentMicros);
        Assert.Empty(initial.Rows);

        var updated = await compute.Record("creator", 500_000L);
        Assert.Equal(500_000L, updated.SpentMicros);
        Assert.Contains(updated.Rows, r => r.Key == "creator" && r.Micros == 500_000L && r.Count == 1L);
    }
}
