using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record ControlHarnessState;

/*
# Control harness screen

A test parent with one child: the control under test, mounted without props (uncontrolled) under the screen's own key. A child takes
commands only from its parent, so the harness sends every command through this screen.
*/
[Neuron("test.control-harness")]
public sealed class ControlHarnessScreen<TControl> : Screen<ControlHarnessState> where TControl : class
{
    protected override View Render(ControlHarnessState state) => new("Harness", Child<TControl>(Id.Key));
}

// Runs one control neuron on TestOS as the one child of a harness screen: send commands to it through its parent, read what it
// published and what it draws.
internal sealed class ControlHarness : IAsyncDisposable
{
    internal static readonly BrainId Brain = new("local");

    private readonly TestOS _os;
    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly string _key;

    internal ControlHarness(Type neuron, string key = "probe", object? state = null)
    {
        _os = TestOS.Local();
        _key = key;
        var register = typeof(TestOS).GetMethod(nameof(TestOS.Register), Type.EmptyTypes)!;
        register.MakeGenericMethod(neuron).Invoke(_os, null);
        register.MakeGenericMethod(typeof(ControlHarnessScreen<>).MakeGenericType(neuron)).Invoke(_os, null);
        var parent = new NeuronId(Brain, "test.control-harness", key);
        Parent = _os.Get<IScreen>(parent);
        Id = new NeuronId(Brain, neuron.GetCustomAttribute<NeuronAttribute>()!.Type, ScreenExtensions.ChildAddress(parent, key));
        if (state is not null) { _os.SeedStoredState(Id, JsonSerializer.Serialize(state, state.GetType(), JsonSerializerOptions.Web)); }
    }

    // The control's address: the harness screen's child.
    internal NeuronId Id { get; }

    // The harness screen: every command reaches the control through it.
    internal IScreen Parent { get; }

    internal Task Send<TCommand>(TCommand command) where TCommand : Signal
        => Dispatch(new Command(_key, AliasOf(typeof(TCommand)), JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    internal Task SendRaw(Signal command)
        => Dispatch(new Command(_key, AliasOf(command.GetType()), JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));

    // Presses the control's node with that key, as a client does: its command (with the field's value) through the parent.
    internal async Task Click(string key, string? value = null)
    {
        _ = await Parent.Read();
        await Parent.ClickChild(_key, key, value);
    }

    internal Task<PublishedView?> Read() => Parent.ReadChild(_key);

    // The domain signals it published: every journaled signal but the screen's own StateChanged / ViewChanged.
    internal Signal[] Published() => [.. _os.Journal(Brain).Signals(Id).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    internal async Task<T> Drawn<T>() where T : Node
    {
        var view = await Read() ?? throw new InvalidOperationException($"{Id} has no view.");
        var root = view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web);
        return Assert.IsType<T>(root);
    }

    // A command to the control (target: its key) goes through the harness screen, read first so its child is routed.
    private async Task Dispatch(Command command)
    {
        _ = await Parent.Read();
        await Parent.Dispatch(command);
    }

    private static string AliasOf(Type signal) => signal.GetCustomAttribute<SignalAttribute>()!.Alias;

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
