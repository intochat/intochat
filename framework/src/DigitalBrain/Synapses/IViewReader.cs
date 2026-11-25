using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Kernel;

namespace DigitalBrain.Units;

/// <summary>
/// The current view of a state alias, read at that alias's state neuron (<c>{brain}/{alias}/default</c>).
/// A view synapse returning a <c>DigitalBrain.UI.View</c> stores it there, as its wire JSON, with a generation that increases on every
/// stored view; the view and its generation are saved with the state, so they survive deactivation and restart.
/// </summary>
[NeuronAlias("synapse.view")]
public interface IViewReader
{
    /// <summary>The current view of <paramref name="alias"/>, or null before the first view (or when the neuron is not that alias's).</summary>
    /// <param name="alias">The state alias.</param>
    [ReadOnlyCall]
    Task<PublishedView?> Read(string alias);
}

/// <summary>The stored view of a state alias.</summary>
/// <param name="Alias">The state alias.</param>
/// <param name="Generation">The generation, increasing with every stored view.</param>
/// <param name="View">The view as wire JSON (the <c>DigitalBrain.UI</c> shape, nodes discriminated by <c>"kind"</c>).</param>
public sealed record PublishedView(string Alias, long Generation, JsonElement View);

/// <summary>A command was refused before any handler ran; nothing was journaled.</summary>
public sealed class ActivationRefusedException : InvalidOperationException
{
    /// <summary>The generation is not the current view's (or there is no view yet).</summary>
    public const string Stale = "Stale";

    /// <summary>No node of the view has the key.</summary>
    public const string UnknownNode = "UnknownNode";

    /// <summary>The keyed node sends no command.</summary>
    public const string NoEmit = "NoEmit";

    /// <summary>A command names a child key that the current view does not name.</summary>
    public const string UnknownChild = "UnknownChild";

    /// <summary>A command names a child address, but the view's owner has no children (a unit or an app without children).</summary>
    public const string NoChildren = "NoChildren";

    /// <summary>A required component slot has no binding and no default.</summary>
    public const string SlotUnbound = "slot.unbound";

    /// <summary>No reason was given (the standard exception constructors).</summary>
    public const string Unspecified = "Unspecified";

    /// <summary>Creates the exception for <paramref name="reason"/>.</summary>
    /// <param name="reason">One of the reason constants.</param>
    public ActivationRefusedException(string reason) : base($"The activation was refused: {reason}.") => Reason = reason;

    /// <summary>Creates the exception for <paramref name="reason"/> with a specific <paramref name="message"/>.</summary>
    /// <param name="reason">One of the reason constants.</param>
    /// <param name="message">The human-readable refusal message.</param>
    public ActivationRefusedException(string reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates the exception.</summary>
    public ActivationRefusedException() : this(Unspecified) { }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The cause.</param>
    public ActivationRefusedException(string message, Exception inner) : base(message, inner) => Reason = Unspecified;

    /// <summary>Why the activation was refused: one of the reason constants.</summary>
    public string Reason { get; }
}

/// <summary>A signal an activated node emitted: journaled under its alias with its JSON payload, delivered to alias reactions only.</summary>
internal sealed record EmittedSignal(string Alias, JsonElement Payload) : Signal, IUntypedSignal
{
    /// <inheritdoc />
    public bool Equals(EmittedSignal? other) => other is not null && string.Equals(Alias, other.Alias, StringComparison.Ordinal)
        && string.Equals(Payload.GetRawText(), other.Payload.GetRawText(), StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Alias);
}
