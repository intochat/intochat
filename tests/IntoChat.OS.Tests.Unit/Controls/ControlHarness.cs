using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// Runs one control neuron on TestOS: dispatch commands to it, read what it published and what it draws.
internal sealed class ControlHarness : IAsyncDisposable
{
    internal static readonly BrainId Brain = new("local");

    private readonly TestOS _os;
    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));

    internal ControlHarness(Type neuron, string key = "probe", object? state = null)
    {
        _os = TestOS.Local();
        typeof(TestOS).GetMethod(nameof(TestOS.Register), Type.EmptyTypes)!.MakeGenericMethod(neuron).Invoke(_os, null);
        Id = new NeuronId(Brain, neuron.GetCustomAttribute<DigitalBrain.Neurons.NeuronAttribute>()!.Type, key);
        if (state is not null) { _os.SeedStoredState(Id, JsonSerializer.Serialize(state, state.GetType(), JsonSerializerOptions.Web)); }
    }

    internal NeuronId Id { get; }

    internal IScreen Screen => _os.Get<IScreen>(Id);

    internal Task Send<TCommand>(TCommand command) where TCommand : Signal
        => Screen.Dispatch(new Command(".", AliasOf(typeof(TCommand)), JsonSerializer.SerializeToElement(command, JsonSerializerOptions.Web)));

    internal Task SendRaw(Signal command)
        => Screen.Dispatch(new Command(".", AliasOf(command.GetType()), JsonSerializer.SerializeToElement(command, command.GetType(), JsonSerializerOptions.Web)));

    // The domain signals it published: every journaled signal but the screen's own StateChanged / ViewChanged.
    internal Signal[] Published() => [.. _os.Journal(Brain).Signals(Id).Select(entry => entry.Signal)
        .Where(signal => signal is not StateChanged && signal is not ViewChanged)];

    internal async Task<T> Drawn<T>() where T : Node
    {
        var view = await Screen.Read() ?? throw new InvalidOperationException($"{Id} has no view.");
        var root = view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web);
        return Assert.IsType<T>(root);
    }

    private static string AliasOf(Type signal) => signal.GetCustomAttribute<SignalAttribute>()!.Alias;

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
