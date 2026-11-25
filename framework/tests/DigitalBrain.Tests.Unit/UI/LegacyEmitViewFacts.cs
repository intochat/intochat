using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Tests.Unit.Neurons;
using DigitalBrain.Tests.Unit.Screens;
using DigitalBrain.Tests.Unit.Synapses;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace DigitalBrain.Tests.Unit.UI;

// A view stored before every action was a command still carries "emit" ({owner, signal, args}). A host reads it as the command it
// means (to ".", same signal and args) when it loads the view, so what it serves names "command" and acts like any other.
public sealed class LegacyEmitViewFacts
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task A_screen_view_stored_with_an_emit_is_served_as_a_command_and_acts_by_its_command()
    {
        var stored = new PublishedView("app.sample-counter", 4, Parse(
            """{"title":"Sample Counter","root":{"kind":"button","key":"btn-inc","label":"Add","emit":{"owner":"app.sample-counter","signal":"counter.increment","args":{"amount":3}}}}"""));
        var screen = new SampleCounterScreen();
        var context = new RecordingContext(new NeuronId(new BrainId("alice"), "app.sample-counter", "default"));
        context.Store(JsonSerializer.SerializeToUtf8Bytes(new StoredState<SampleCounterState>(new SampleCounterState { Count = 1 }, stored),
            RuntimeProtocol.OptionsFor(typeof(StoredState<SampleCounterState>))));
        NeuronAttachment.Attach(screen, context);
        await NeuronAttachment.Activate(screen, CancellationToken.None);

        var served = (await screen.Read())!;
        var button = served.View.GetProperty("root");
        Assert.False(button.TryGetProperty("emit", out _));
        var command = JsonSerializer.Deserialize<Command>(button.GetProperty("command").GetRawText(), RuntimeProtocol.OptionsFor(typeof(Command)))!;
        Assert.Equal((".", "counter.increment", 3), (command.Target, command.Signal, command.Args!.Value.GetProperty("amount").GetInt32()));

        // What the client sends for that node over POST /act: the screen's Dispatch of the served command.
        await screen.Dispatch(command);

        Assert.Contains("Count: 4", (await screen.Read())!.View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_unit_view_stored_with_an_emit_is_served_as_a_command_and_its_key_acts()
    {
        var id = new NeuronId(new BrainId("alice"), "ledger", "default");
        var context = new RecordingContext(id);
        var stored = new PublishedView("ledger", 2, Parse(
            """{"title":"ledger","root":{"kind":"button","key":"charge","label":"Charge","emit":{"owner":"ledger","signal":"test.charging","args":{"amount":5}}}}"""));
        context.Store(new StoredUnit<Ledger>(new Ledger(1m), stored));
        var unit = new UnitNeuron<Ledger>(new SynapseCatalogSource(SynapseCatalog.Scan(new[] { typeof(Ledger), typeof(Books) })), new UnboundSlotResolver());
        NeuronAttachment.Attach(unit, context);
        await NeuronAttachment.Activate(unit, CancellationToken.None);

        var served = (await unit.Read("ledger"))!;
        var button = served.View.GetProperty("root");
        Assert.False(button.TryGetProperty("emit", out _));
        Assert.Equal(".", button.GetProperty("command").GetProperty("target").GetString());

        context.Caller = DigitalBrain.Access.EdgeAuthority.CreateInternal().Person("alice", "alice-account", id.Brain);
        await unit.Click("charge");

        var said = Assert.Single(context.Published.OfType<EmittedSignal>());
        Assert.Equal(("test.charging", 5, "alice"), (said.Alias, said.Payload.GetProperty("amount").GetInt32(), said.Payload.GetProperty("by").GetString()));
    }

    [Fact]
    public void Normalize_reads_an_emit_and_an_owner_submit_as_commands_and_leaves_args_alone()
    {
        var legacy = Parse("""
            {"root":{"kind":"column","children":[
              {"kind":"navItem","key":"n","emit":null},
              {"kind":"conversation","composer":{"key":"c","onSubmit":{"owner":"app.ino","signal":"ino.asked"}}},
              {"kind":"button","key":"b","emit":{"owner":"x","signal":"s.go","args":{"emit":{"owner":"kept","signal":"data"}}}}
            ]}}
            """);

        var read = LegacyViewJson.Normalize(legacy).GetProperty("root").GetProperty("children");

        Assert.False(read[0].TryGetProperty("emit", out _));
        Assert.Equal(""".""", read[1].GetProperty("composer").GetProperty("onSubmit").GetProperty("target").GetString());
        Assert.False(read[1].GetProperty("composer").GetProperty("onSubmit").TryGetProperty("owner", out _));
        var go = read[2].GetProperty("command");
        Assert.Equal(("s.go", "kept"), (go.GetProperty("signal").GetString(), go.GetProperty("args").GetProperty("emit").GetProperty("owner").GetString()));
        Assert.False(read[2].TryGetProperty("emit", out _));
    }

    [Fact]
    public void Normalize_returns_a_current_view_unchanged()
    {
        var current = Parse("""{"root":{"kind":"button","key":"b","command":{"target":".","signal":"s.go"}}}""");

        Assert.Equal(current.GetRawText(), LegacyViewJson.Normalize(current).GetRawText());
    }
}
