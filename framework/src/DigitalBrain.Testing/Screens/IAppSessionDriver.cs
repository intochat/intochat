using DigitalBrain.Units;

namespace DigitalBrain.Testing;

/// <summary>
/// Drives a screen session (reading views, sending the command of a keyed node, sending typed signals, and waiting for idle).
/// </summary>
public interface IAppSessionDriver : IAsyncDisposable
{
    /// <summary>Reads the screen's current published view.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<PublishedView> ReadViewAsync(CancellationToken ct = default);

    /// <summary>Sends the command of the keyed node in the current view through the screen's Dispatch and returns the updated view.</summary>
    /// <param name="key">The node key in the current view.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PublishedView> ClickAsync(string key, CancellationToken ct = default);

    /// <summary>Submits a text field value to a keyed input node in the current view and returns the updated view.</summary>
    /// <param name="key">The input node key in the current view.</param>
    /// <param name="text">The text value to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PublishedView> TypeAsync(string key, string text, CancellationToken ct = default);

    /// <summary>Sends a typed signal to the screen, waits for idle, and returns the updated view.</summary>
    /// <typeparam name="TSignal">The signal type.</typeparam>
    /// <param name="signal">The signal instance.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PublishedView> SendAsync<TSignal>(TSignal signal, CancellationToken ct = default) where TSignal : Signal;

    /// <summary>Waits until all queued turns, deliveries, and ticks have settled.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task WaitForIdleAsync(CancellationToken ct = default);
}
