using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.Units;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

/// <summary>
/// An <see cref="IAppSessionDriver"/> that drives a screen over HTTP against a running <see cref="OsAppHostFixture"/>.
/// </summary>
public sealed class AspireHttpSessionDriver : IAppSessionDriver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly OsAppHostFixture _host;
    private readonly string _app;
    private readonly string _scope;
    private PublishedView? _lastView;
    private bool _disposed;

    public AspireHttpSessionDriver(OsAppHostFixture host, string app, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(app);

        _host = host;
        _app = app;
        _scope = string.IsNullOrWhiteSpace(scope) ? host.Scope : scope;
    }

    private string ViewNodeName => $"{_scope}/apps/{_app}/view";

    private string ViewNodeUrl => $"/brains/{_scope}/apps/node?kind=view&name={Uri.EscapeDataString(ViewNodeName)}";

    /// <summary>Opens the app by posting an <c>open</c> request and waiting for its view to publish.</summary>
    public async Task<PublishedView> OpenAsync(string input = "{}", CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var baseline = await TryReadViewAsync(ct).ConfigureAwait(false);
        using var response = await _host.Os.PostAsJsonAsync(
            $"/brains/{_scope}/apps/{_app}/request",
            new { operation = "open", input },
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await WaitForSettledViewAsync(baseline?.Generation ?? 0, ct).ConfigureAwait(false);
        return await ReadViewAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PublishedView> ReadViewAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var published = await TryReadViewAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The screen '{_app}' returned no published view from '{ViewNodeUrl}'.");
        _lastView = published;
        return published;
    }

    /// <inheritdoc />
    public Task<PublishedView> ActivateAsync(string key, CancellationToken ct = default) => PressAsync(key, value: null, ct);

    /// <inheritdoc />
    public Task<PublishedView> TypeAsync(string key, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PressAsync(key, text, ct);
    }

    // What the shell does: it sends the keyed node's command (a field's onSubmit, an info bar's close) from the view it read to
    // POST /act for this app's screen, with a field's text as the value.
    private async Task<PublishedView> PressAsync(string key, string? value, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var current = _lastView ?? await ReadViewAsync(ct).ConfigureAwait(false);
        var command = CommandOf(current.View, key)
            ?? throw new InvalidOperationException($"The view of '{_app}' has no node '{key}' that sends a command.");
        using var response = await _host.Os.PostAsJsonAsync(
            $"/brains/{_scope}/act",
            new
            {
                neuron = $"{_scope}/apps/{_app}",
                target = command.GetProperty("target").GetString(),
                signal = command.GetProperty("signal").GetString(),
                args = command.TryGetProperty("args", out var args) ? args.Clone() : (JsonElement?)null,
                value,
            },
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await WaitForSettledViewAsync(current.Generation, ct).ConfigureAwait(false);
        return await ReadViewAsync(ct).ConfigureAwait(false);
    }

    private static JsonElement? CommandOf(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key)
            {
                foreach (var member in new[] { "command", "onSubmit", "close" })
                {
                    if (element.TryGetProperty(member, out var command) && command.ValueKind == JsonValueKind.Object) { return command.Clone(); }
                }
            }
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is not ("command" or "onSubmit" or "close" or "args") && CommandOf(property.Value, key) is { } found) { return found; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (CommandOf(item, key) is { } found) { return found; }
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<PublishedView> SendAsync<TSignal>(TSignal signal, CancellationToken ct = default) where TSignal : Signal
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(signal);
        ct.ThrowIfCancellationRequested();

        var baseline = _lastView?.Generation ?? 0;
        var operation = JsonNamingPolicy.CamelCase.ConvertName(typeof(TSignal).Name);
        var input = JsonSerializer.Serialize(signal, typeof(TSignal), JsonOptions);

        using var response = await _host.Os.PostAsJsonAsync(
            $"/brains/{_scope}/apps/{_app}/request",
            new { operation, input },
            ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await WaitForSettledViewAsync(baseline, ct).ConfigureAwait(false);
        return await ReadViewAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WaitForIdleAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        await WaitForSettledViewAsync(_lastView?.Generation ?? 0, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task<PublishedView?> TryReadViewAsync(CancellationToken ct)
    {
        using var response = await _host.Os.GetAsync(ViewNodeUrl, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("view", out var viewElem)
            || viewElem.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var alias = root.TryGetProperty("alias", out var aliasElem) && aliasElem.ValueKind == JsonValueKind.String
            ? aliasElem.GetString() ?? $"app.{_app}"
            : $"app.{_app}";
        var generation = root.TryGetProperty("generation", out var genElem) && genElem.TryGetInt64(out var gen)
            ? gen
            : 0L;

        return new PublishedView(alias, generation, viewElem.Clone());
    }

    private async Task WaitForSettledViewAsync(long baselineGeneration, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        string? lastViewRaw = null;
        var unchangedPolls = 0;

        while (!deadline.Token.IsCancellationRequested)
        {
            var current = await TryReadViewAsync(deadline.Token).ConfigureAwait(false);
            if (current is not null)
            {
                var viewRaw = current.View.GetRawText();
                if (string.Equals(viewRaw, lastViewRaw, StringComparison.Ordinal))
                {
                    unchangedPolls++;
                }
                else
                {
                    lastViewRaw = viewRaw;
                    unchangedPolls = 0;
                }

                if ((current.Generation > baselineGeneration && unchangedPolls >= 2) || unchangedPolls >= 10)
                {
                    _lastView = current;
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
        }
    }
}
