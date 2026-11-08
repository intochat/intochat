using DigitalBrain.Contracts;
using DigitalBrain;
using DigitalBrain.UI;


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
    [DigitalBrain.ReadOnlyCall] Task<PublishedView?> Read();

    /// <summary>Resolves the node keyed <paramref name="key"/> in the view of <paramref name="generation"/> to what it does; the
    /// caller (the edge) routes it. A stale generation or an unknown or disabled node is refused with a stale action.</summary>
    [DigitalBrain.ReadOnlyCall] Task<ViewActivation> Activate(long generation, string key, string? fieldValue);
}

/// <summary>What an activated view node does.</summary>
public enum ViewActivationKind { None = 0, Emit = 1, Intent = 2, Resolve = 3 }

/// <summary>An activated node's action: an <see cref="DigitalBrain.UI.Emit"/> for its owner, a client-local
/// <see cref="DigitalBrain.UI.Intent"/>, or an approval's <see cref="DigitalBrain.UI.Resolve"/>, with the field value when given.</summary>
public sealed record ViewActivation(ViewActivationKind Kind, Emit? Emit = null, Intent? Intent = null, Resolve? Resolve = null, string? Value = null)
{
    public static readonly ViewActivation None = new(ViewActivationKind.None);
}

[Signal("ui.view-changed")]
public sealed record ViewChanged(string Name, long Generation) : DigitalBrain.Signal;
