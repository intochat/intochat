using System.Text.Json;
using DigitalBrain.Units;
using Xunit;

namespace DigitalBrain.Testing;

/// <summary>
/// A typed TDD session for driving a screen and asserting on its rendered view.
/// </summary>
/// <typeparam name="TScreen">The concrete screen type under test.</typeparam>
public sealed class AppSession<TScreen> : IAsyncDisposable
{
    private static readonly HashSet<string> TextProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "title",
        "label",
        "value",
        "subtitle",
        "message",
        "description",
        "placeholder",
        "content",
        "heading",
        "caption",
        "badge",
    };

    private readonly IAppSessionDriver _driver;

    /// <summary>Creates a session wrapping <paramref name="driver"/> with its initial <paramref name="initialView"/>.</summary>
    /// <param name="driver">The underlying session driver.</param>
    /// <param name="initialView">The initial published view read when opening the session.</param>
    public AppSession(IAppSessionDriver driver, PublishedView initialView)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        CurrentView = initialView ?? throw new ArgumentNullException(nameof(initialView));
    }

    /// <summary>The most recently observed published view.</summary>
    public PublishedView CurrentView { get; private set; }

    /// <summary>Activates the node with <paramref name="key"/> in the current view and updates <see cref="CurrentView"/>.</summary>
    /// <param name="key">The node key to activate.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PublishedView> Click(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        CurrentView = await _driver.ActivateAsync(key, ct).ConfigureAwait(false);
        return CurrentView;
    }

    /// <summary>Sends a typed signal to the screen and updates <see cref="CurrentView"/>.</summary>
    /// <typeparam name="TSignal">The signal type.</typeparam>
    /// <param name="signal">The signal instance.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PublishedView> Send<TSignal>(TSignal signal, CancellationToken ct = default) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        CurrentView = await _driver.SendAsync(signal, ct).ConfigureAwait(false);
        return CurrentView;
    }

    /// <summary>Re-reads the screen's current published view and updates <see cref="CurrentView"/>.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PublishedView> Refresh(CancellationToken ct = default)
    {
        CurrentView = await _driver.ReadViewAsync(ct).ConfigureAwait(false);
        return CurrentView;
    }

    /// <summary>Waits until the underlying runtime is idle.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task WaitForIdleAsync(CancellationToken ct = default) => _driver.WaitForIdleAsync(ct);

    /// <summary>Asserts that <see cref="CurrentView"/> displays <paramref name="expectedText"/>.</summary>
    /// <param name="expectedText">The expected text substring.</param>
    public AppSession<TScreen> ShouldDisplayText(string expectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedText);
        var rendered = ExtractRenderedText(CurrentView.View);
        Assert.True(
            rendered.Contains(expectedText, StringComparison.Ordinal),
            $"Expected view of {typeof(TScreen).Name} (generation {CurrentView.Generation}) to display \"{expectedText}\", but rendered text was: \"{rendered}\".");
        return this;
    }

    /// <summary>Asserts that <see cref="CurrentView"/> does not display <paramref name="unexpectedText"/>.</summary>
    /// <param name="unexpectedText">The text substring that must not appear.</param>
    public AppSession<TScreen> ShouldNotDisplayText(string unexpectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(unexpectedText);
        var rendered = ExtractRenderedText(CurrentView.View);
        Assert.False(
            rendered.Contains(unexpectedText, StringComparison.Ordinal),
            $"Expected view of {typeof(TScreen).Name} (generation {CurrentView.Generation}) not to display \"{unexpectedText}\", but rendered text was: \"{rendered}\".");
        return this;
    }

    /// <summary>Asserts that <see cref="CurrentView"/> has generation <paramref name="expectedGeneration"/>.</summary>
    /// <param name="expectedGeneration">The expected view generation.</param>
    public AppSession<TScreen> ShouldHaveGeneration(long expectedGeneration)
    {
        Assert.Equal(expectedGeneration, CurrentView.Generation);
        return this;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    private static string ExtractRenderedText(JsonElement view)
    {
        var parts = new List<string>();
        CollectText(view, parts);
        return string.Join(" | ", parts);
    }

    private static void CollectText(JsonElement element, List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && TextProperties.Contains(property.Name))
                    {
                        var text = property.Value.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            parts.Add(text);
                        }
                    }
                    else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectText(property.Value, parts);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectText(item, parts);
                }
                break;
        }
    }
}

/// <summary>
/// Factory helpers for opening <see cref="AppSession{TScreen}"/> instances against a runtime or <see cref="TestOS"/>.
/// </summary>
public static class AppSession
{
    /// <summary>Opens a new <see cref="AppSession{TScreen}"/> against any <see cref="INeuronRuntime"/>.</summary>
    /// <typeparam name="TScreen">The concrete screen type to open.</typeparam>
    /// <param name="runtime">The neuron runtime (in-memory <see cref="TestOS"/> or Orleans silo).</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AppSession<TScreen>> OpenAsync<TScreen>(
        INeuronRuntime runtime,
        string? key = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var driver = new DirectNeuronSessionDriver(runtime, typeof(TScreen), key);
        try
        {
            var initialView = await driver.ReadViewAsync(ct).ConfigureAwait(false);
            return new AppSession<TScreen>(driver, initialView);
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a new <see cref="AppSession{TScreen}"/> directly against a <see cref="TestOS"/> instance.</summary>
    /// <typeparam name="TScreen">The concrete screen type to open.</typeparam>
    /// <param name="os">The in-memory <see cref="TestOS"/>.</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    /// <param name="brain">Optional brain id; defaults to <c>"test"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<AppSession<TScreen>> OpenAsync<TScreen>(
        TestOS os,
        string? key = null,
        BrainId? brain = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(os);
        var driver = new DirectNeuronSessionDriver(os, typeof(TScreen), key, brain);
        try
        {
            var initialView = await driver.ReadViewAsync(ct).ConfigureAwait(false);
            return new AppSession<TScreen>(driver, initialView);
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
