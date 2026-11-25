using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;
using DigitalBrain.Units;


namespace IntoChat.OS.Modules.Flutter.Views;

/// <summary>
/// One app view: a <see cref="View"/> stored as its wire JSON, exactly as the client renders it. Every changed publication
/// increases the neuron's own generation; an activation names the generation it saw and the key of the node it activated.
/// </summary>
[NeuronAlias("view"), DefaultNeuronType(UIVocabulary.ViewType)]
public interface IView
{
    /// <summary>Stores <paramref name="view"/>. A publication from an older <paramref name="generation"/> (the publisher's program
    /// generation) than the stored one is ignored; an unchanged view keeps its generation.</summary>
    Task Publish(long generation, View view);

    /// <summary>The stored view and its generation, or null before the first publication.</summary>
    [DigitalBrain.Contracts.ReadOnlyCall] Task<PublishedView?> Read();

    /// <summary>Resolves the node keyed <paramref name="key"/> in the view of <paramref name="generation"/> to what it does; the
    /// caller (the edge) routes it. A stale generation or an unknown or disabled node is refused with a stale action.</summary>
    [DigitalBrain.Contracts.ReadOnlyCall] Task<ViewActivation> Activate(long generation, string key, string? fieldValue);
}

/// <summary>What an activated view node does. 1 was the retired emit and is never reused.</summary>
public enum ViewActivationKind { None = 0, Intent = 2, Resolve = 3, Command = 4 }

/// <summary>An activated node's action: a client-local <see cref="DigitalBrain.UI.Intent"/>, an approval's <see cref="DigitalBrain.UI.Resolve"/>,
/// or its <see cref="DigitalBrain.UI.Command"/> (to "." for the view's own app, or to a child address), with the field value when given.</summary>
public sealed record ViewActivation(ViewActivationKind Kind, Intent? Intent = null, Resolve? Resolve = null, string? Value = null, Command? Command = null)
{
    public static readonly ViewActivation None = new(ViewActivationKind.None);
}

[Signal("ui.view-changed")]
public sealed record FlutterViewChanged(string Name, long Generation) : DigitalBrain.Signal;
