using DigitalBrain;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Fields;
using DigitalBrain.Testing;

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
        var id = runtime.Id(ChatIndex.GrainType, "apps/ino-data/chats");
        var index = runtime.Get<IChatIndex>(id);

        await index.Create("main", ChatIndex.NewChat);
        await index.Touch("main", "  plan   the week  ");
        await index.Archive("main");
        await Assert.ThrowsAsync<ArgumentException>(() => index.Archive("not-an-id"));

        Assert.Equal("plan the week", (await index.Get("main"))!.Title);
        Assert.Empty((await index.List(archived: false)).Entries);
        Assert.Equal("main", Assert.Single((await index.List(archived: true)).Entries).Id);
        var changes = await runtime.WaitForSignals<ChatIndexChanged>(id, 3, Within, ct);
        Assert.All(changes, change => Assert.Equal($"{id.Brain.Value}/apps/ino-data/chats", change.Name));
        Assert.Equal([1L, 2L, 3L], changes.Select(change => change.Revision));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task ComputeNeuronReturnsDefaultUsageAndRecordsNewUsage(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        var id = runtime.Id("ui.compute", "usage");
        var compute = runtime.Get<IntoChat.OS.Modules.Flutter.Compute.ICompute>(id);

        var initial = await compute.Read();
        Assert.Equal(1_970_000L, initial.SpentMicros);
        Assert.Equal(3, initial.Rows.Length);

        var updated = await compute.Record("creator", 500_000L);
        Assert.Equal(2_470_000L, updated.SpentMicros);
        Assert.Contains(updated.Rows, r => r.Key == "creator" && r.Micros == 1_180_000L && r.Count == 15L);
    }
}
