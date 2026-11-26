using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using DigitalBrain.Units;


namespace IntoChat.OS.Modules.Flutter.Views;

/// <summary>
/// One app view: a <see cref="View"/> stored as its wire JSON, exactly as the client renders it. Every changed publication
/// increases the neuron's own generation. Commands reach the owning app through the view neuron's <see cref="IScreen.Dispatch"/>.
/// </summary>
[NeuronAlias("view"), DefaultNeuronType(UIVocabulary.ViewType)]
public interface IView
{
    /// <summary>Stores <paramref name="view"/>. A publication from an older <paramref name="generation"/> (the publisher's program
    /// generation) than the stored one is ignored; an unchanged view keeps its generation.</summary>
    Task Publish(long generation, View view);

    /// <summary>The stored view and its generation, or null before the first publication.</summary>
    [DigitalBrain.Contracts.ReadOnlyCall] Task<PublishedView?> Read();
}

[Signal("ui.view-changed")]
public sealed record FlutterViewChanged(string Name, long Generation) : DigitalBrain.Signal;
