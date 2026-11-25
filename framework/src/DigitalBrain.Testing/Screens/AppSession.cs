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
        "uiValue",
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

    /// <summary>Sends the command of the node with <paramref name="key"/> in the current view and updates <see cref="CurrentView"/>.</summary>
    /// <param name="key">The node key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PublishedView> Click(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        CurrentView = await _driver.ClickAsync(key, ct).ConfigureAwait(false);
        return CurrentView;
    }

    /// <summary>Types <paramref name="text"/> into the input node with <paramref name="key"/> and updates <see cref="CurrentView"/>.</summary>
    /// <param name="key">The input node key.</param>
    /// <param name="text">The text value to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PublishedView> Type(string key, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(text);
        CurrentView = await _driver.TypeAsync(key, text, ct).ConfigureAwait(false);
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

    /// <summary>Asserts that <see cref="CurrentView"/> does not have an enabled node with <paramref name="key"/>.</summary>
    /// <param name="key">The node key that must be absent or disabled.</param>
    public AppSession<TScreen> ShouldNotHaveEnabled(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (FindNodeByKey(CurrentView.View, key) is { } node)
        {
            var isEnabled = !node.TryGetProperty("enabled", out var enabledProp)
                || enabledProp.ValueKind != JsonValueKind.False;
            Assert.False(
                isEnabled,
                $"Expected node '{key}' in view of {typeof(TScreen).Name} (generation {CurrentView.Generation}) not to be enabled, but it was enabled.");
        }
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

    private static JsonElement? FindNodeByKey(JsonElement element, string key)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("key", out var keyProp)
                    && keyProp.ValueKind == JsonValueKind.String
                    && string.Equals(keyProp.GetString(), key, StringComparison.Ordinal))
                {
                    return element;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && FindNodeByKey(property.Value, key) is { } found)
                    {
                        return found;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindNodeByKey(item, key) is { } found)
                    {
                        return found;
                    }
                }
                break;
        }

        return null;
    }

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
                    else if (property.Value.ValueKind == JsonValueKind.Array
                        && string.Equals(property.Name, "rows", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectTableRows(property.Value, parts);
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

    private static void CollectTableRows(JsonElement rows, List<string> parts)
    {
        foreach (var item in rows.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    var text = item.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(text);
                    }
                    break;
                case JsonValueKind.Array:
                    CollectTableRows(item, parts);
                    break;
                case JsonValueKind.Object:
                    CollectText(item, parts);
                    break;
            }
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
    public static Task<AppSession<TScreen>> OpenAsync<TScreen>(
        INeuronRuntime runtime,
        string? key = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return Open<TScreen>(new DirectNeuronSessionDriver(runtime, typeof(TScreen), key), ct);
    }

    /// <summary>Opens a new <see cref="AppSession{TScreen}"/> against any <see cref="INeuronRuntime"/> for a specific <paramref name="brain"/> and <paramref name="caller"/>.</summary>
    /// <typeparam name="TScreen">The concrete screen type to open.</typeparam>
    /// <param name="runtime">The neuron runtime (in-memory <see cref="TestOS"/> or Orleans silo).</param>
    /// <param name="brain">The brain hosting the screen.</param>
    /// <param name="caller">The caller stamped on screen operations.</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<AppSession<TScreen>> OpenAsync<TScreen>(
        INeuronRuntime runtime,
        BrainId brain,
        DigitalBrain.Access.Caller caller,
        string? key = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(caller);
        return Open<TScreen>(new DirectNeuronSessionDriver(runtime, typeof(TScreen), key, brain, caller), ct);
    }

    /// <summary>Opens a new <see cref="AppSession{TScreen}"/> directly against a <see cref="TestOS"/> instance.</summary>
    /// <typeparam name="TScreen">The concrete screen type to open.</typeparam>
    /// <param name="os">The in-memory <see cref="TestOS"/>.</param>
    /// <param name="key">Optional screen neuron key; when omitted, an isolated unique key is generated.</param>
    /// <param name="brain">Optional brain id; defaults to <c>"test"</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<AppSession<TScreen>> OpenAsync<TScreen>(
        TestOS os,
        string? key = null,
        BrainId? brain = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(os);
        return Open<TScreen>(new DirectNeuronSessionDriver(os, typeof(TScreen), key, brain), ct);
    }

    // Reads the first view; a session that cannot open disposes its driver.
    private static async Task<AppSession<TScreen>> Open<TScreen>(DirectNeuronSessionDriver driver, CancellationToken ct)
    {
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
