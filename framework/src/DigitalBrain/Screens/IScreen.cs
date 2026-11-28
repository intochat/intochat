using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Units;

namespace DigitalBrain;

/// <summary>
/// A screen: a neuron that holds domain state and renders a declarative UI View. It is read with Read, Open and ReadChild and takes
/// commands only through Dispatch (and Request): a caller that picks a node by key sends that node's command (<see cref="PublishedViews.CommandAt"/>).
/// </summary>
[Shareable, Offered, NeuronAlias("screen")]
public interface IScreen
{
    /// <summary>Opens the screen, returning its current published view.</summary>
    [ReadOnlyCall]
    Task<PublishedView?> Open();

    /// <summary>Reads the current published view of this screen; null if uninitialized or alias mismatches.</summary>
    /// <param name="alias">The view alias (defaults to the screen neuron type).</param>
    [Shared, ReadOnlyCall]
    Task<PublishedView?> Read(string? alias = null);

    /// <summary>
    /// Dispatches an external signal request to this screen one-way without waiting for reaction completion. A child screen (its key is
    /// a child address) refuses a request from anything but its parent (<c>control.notFromParent</c>).
    /// </summary>
    /// <param name="signal">The journal alias of the signal.</param>
    /// <param name="args">Optional JSON payload for the signal constructor/properties.</param>
    [OneWayCall]
    Task Request(string signal, JsonElement? args = null);

    /// <summary>
    /// Routes <paramref name="command"/> through this screen and awaits its handling: "." is the screen itself, any other target a
    /// child key its current view shows, or a key path "a/b" that each child's own Dispatch resolves one hop further. The parent
    /// owns its children, so a client acts on a child only through this call; a handler's failure reaches the caller. A child screen
    /// takes a command only from its parent, which applied the child's gates on the way.
    /// </summary>
    /// <exception cref="ActivationRefusedException">The current view names no child with that key (<see cref="ActivationRefusedException.UnknownChild"/>),
    /// or this screen is a child and the caller is not its parent (<c>control.notFromParent</c>).</exception>
    [Shared]
    Task Dispatch(DigitalBrain.UI.Command command);

    /// <summary>
    /// Reads the view of the child at a key path ("a" or "a/b") of this screen, routed hop by hop as <see cref="Dispatch"/> routes a
    /// command: each parent resolves only a key its current view shows. A client reads a child only through its top-level screen.
    /// </summary>
    /// <param name="path">The key path, relative to this screen.</param>
    /// <exception cref="ActivationRefusedException">A hop names no child of its view (<see cref="ActivationRefusedException.UnknownChild"/>).</exception>
    /// <exception cref="ArgumentException">The path is empty or has an empty, "." or ".." hop.</exception>
    [Shared, ReadOnlyCall]
    Task<PublishedView?> ReadChild(string path);
}

/// <summary>
/// Extension methods for interacting with screens.
/// </summary>
public static class ScreenExtensions
{
    /// <summary>
    /// Dispatches a typed signal request to this screen one-way without waiting for reaction completion.
    /// </summary>
    /// <param name="screen">The target screen.</param>
    /// <param name="signal">The signal instance.</param>
    public static Task Request<TSignal>(this IScreen screen, TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(signal);
        return screen.Request(SignalAliases.Of(typeof(TSignal)), PooledJson.Encode(signal, typeof(TSignal)));
    }

    /// <summary>The address of the child <paramref name="key"/> of <paramref name="parent"/>: the parent's id, '/', the key. It is the child's neuron key.</summary>
    /// <exception cref="ArgumentException">The key is empty, ".", ".." or contains '/': it would collide with another parent's child.</exception>
    public static string ChildAddress(NeuronId parent, string key)
    {
        CheckChildKey(key);
        return $"{parent}/{key}";
    }

    internal static void CheckChildKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || key is "." or ".." || key.Contains('/', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException($"Child key '{key}' is empty, '.', '..' or contains '/'.", nameof(key));
        }
    }
}
