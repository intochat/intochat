using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

// AppStateV1.bin is an AppState the store wrote before the one-caller model (see AppStateV1Capture.cs): its "principal" is the
// old caller. Owner decision 1 (17 Nov): it is read once into the publisher source, and no identity is kept.
public sealed class AppStateV1MigrationFacts
{
    private static readonly BrainId Owner = new("owner");

    private static byte[] Sample() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "AppStateV1.bin"));

    [Fact]
    public void TheStoreSerializerReadsTheV1PrincipalIntoTheLegacyShape()
    {
        var state = JsonSerializer.Deserialize<AppState>(Sample(), RuntimeProtocol.OptionsFor(typeof(AppState)))!;

        Assert.Equal(new StoredPrincipalV1("owner", "owner", "owner", "owner/apps/demo", "c1"), state.LegacyPrincipal);
        Assert.Null(state.PublisherSource);
    }

    [Fact]
    public async Task StoredV1PrincipalBecomesThePublisher()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
        var id = new NeuronId(Owner, IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(Owner, "demo").Key);
        os.SeedStoredState(id, System.Text.Encoding.UTF8.GetString(Sample()));

        using (os.Edge.Enter(os.Edge.Person("owner", "owner", Owner)))
        {
            await os.Get<IApp>(id).Read();
        }
        await os.Idle();

        var raw = StoredJson(os.Journal(Owner), id);
        var stored = raw.Deserialize<AppState>(RuntimeProtocol.OptionsFor(typeof(AppState)))!;
        Assert.Equal(new AppPublisher("owner", "owner"), stored.PublisherSource);
        Assert.Null(stored.LegacyPrincipal);
        Assert.False(raw.TryGetProperty("principal", out _), "The migrated record no longer writes the old caller.");
    }

    // The record as the store now holds it. TestOS keeps its state reader internal, so the fact reaches it by reflection.
    private static JsonElement StoredJson(TestJournal journal, NeuronId id)
    {
        var load = typeof(TestJournal).GetMethod("Load", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(JsonElement));
        return (JsonElement)load.Invoke(journal, [id])!;
    }

    [Fact]
    public void MigrationKeepsOnlyTheAccountAndBrain()
    {
        var state = new AppState { LegacyPrincipal = new StoredPrincipalV1("someone", "acct", "brain-1", "brain-1/apps/x", "c9") };

        Assert.True(state.MigrateLegacyPrincipal());
        Assert.Equal(new AppPublisher("acct", "brain-1"), state.PublisherSource);
        Assert.Null(state.LegacyPrincipal);
        Assert.False(state.MigrateLegacyPrincipal());
    }
}
