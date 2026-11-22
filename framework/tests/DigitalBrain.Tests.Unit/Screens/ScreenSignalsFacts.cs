using System.Reflection.Emit;
using System.Runtime.Loader;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Tests.Unit.Neurons;
using DigitalBrain.UI;
using Xunit;

namespace DigitalBrain.Tests.Unit.Screens;

public sealed record FirstSignal : Signal;

[Signal("screen.second")]
public sealed record SecondSignal : Signal;

public sealed class DummyScreenState { }

public class DummyScreen : Screen<DummyScreenState>
{
    protected override View Render(DummyScreenState state) => new("Dummy", new Column([]));

    [On]
    public Task React(FirstSignal signal) { _ = State; return Task.CompletedTask; }

    [On]
    public Task React(SecondSignal signal) { _ = State; return Task.CompletedTask; }
}

[Signal("screen.registered-ping")]
public sealed record RegisteredPing(string Message) : Signal;

public sealed record RegisteredDispatchState
{
    public string LastMessage { get; init; } = "";
}

public sealed class RegisteredDispatchScreen : Screen<RegisteredDispatchState>
{
    public int HandlerCalls { get; private set; }

    protected override View Render(RegisteredDispatchState state)
        => new("Registered", new Heading(state.LastMessage));

    [On]
    public Task React(RegisteredPing signal)
    {
        HandlerCalls++;
        return Show(State with { LastMessage = signal.Message });
    }
}

public sealed class ScreenSignalsFacts
{
    [Fact]
    public void For_DiscoversAllOnMethodsByJournalAlias()
    {
        var map = ScreenSignals.For(typeof(DummyScreen));
        Assert.NotNull(map);
        Assert.True(map.ContainsKey(SignalAliases.Of(typeof(FirstSignal))));
        Assert.True(map.ContainsKey("screen.second"));
        Assert.Equal(typeof(FirstSignal), map[SignalAliases.Of(typeof(FirstSignal))]);
        Assert.Equal(typeof(SecondSignal), map["screen.second"]);
    }

    [Fact]
    public void Find_ReturnsSignalTypeForKnownAlias_AndNullForUnknown()
    {
        var type = ScreenSignals.Find(typeof(DummyScreen), "screen.second");
        Assert.Equal(typeof(SecondSignal), type);

        var unknown = ScreenSignals.Find(typeof(DummyScreen), "unknown.alias");
        Assert.Null(unknown);
    }

    [Fact]
    public void CollectibleScreen_ScansWithoutPinningStaticCache()
    {
        var alc = new AssemblyLoadContext("CollectibleContext", isCollectible: true);
        try
        {
            using var stream = File.OpenRead(typeof(DummyScreen).Assembly.Location);
            var loadedAssembly = alc.LoadFromStream(stream);
            var loadedType = loadedAssembly.GetType(typeof(DummyScreen).FullName!)!;

            Assert.True(loadedType.IsCollectible);

            var found = ScreenSignals.Find(loadedType, "screen.second");
            Assert.NotNull(found);
            Assert.Equal(nameof(SecondSignal), found.Name);
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public async Task RegisteredHandler_DispatchesDirectlyThroughRegisteredTable()
    {
        var dispatchedViaRegisteredTable = false;
        ScreenSignals.Register(
            typeof(RegisteredDispatchScreen),
            new Dictionary<string, ScreenSignalHandler>(StringComparer.Ordinal)
            {
                ["screen.registered-ping"] = new ScreenSignalHandler(
                    typeof(RegisteredPing),
                    (screen, signal, _) =>
                    {
                        dispatchedViaRegisteredTable = true;
                        return ((RegisteredDispatchScreen)screen).React((RegisteredPing)signal);
                    }),
            });

        Assert.Equal(typeof(RegisteredPing), ScreenSignals.Find(typeof(RegisteredDispatchScreen), "screen.registered-ping"));
        Assert.NotNull(ScreenSignals.FindHandler(typeof(RegisteredDispatchScreen), "screen.registered-ping"));

        var screen = new RegisteredDispatchScreen();
        var id = new NeuronId(new BrainId("alice"), "app.registered-dispatch", "default");
        var context = new RecordingContext(id);
        NeuronAttachment.Attach(screen, context);
        await NeuronAttachment.Activate(screen, CancellationToken.None);

        await screen.Request(new RegisteredPing("direct-dispatch"));

        Assert.True(dispatchedViaRegisteredTable);
        Assert.Equal(1, screen.HandlerCalls);
        var view = await screen.Read();
        Assert.NotNull(view);
        Assert.Contains("direct-dispatch", view.View.GetRawText(), StringComparison.Ordinal);
    }
}
