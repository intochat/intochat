using System.Text.Json;
using System.Text.RegularExpressions;
using IntoChat.OS.Framework;
using IntoChat.OS.Tools;

namespace IntoChat.OS.Microsoft.Playwright;

[GenerateSerializer, Alias("playwright.page-tools-store")]
public sealed class PageToolsStore
{
    [Id(0)] public PageToolsState? State { get; set; }
}

[GrainType("playwright.page-tools")]
internal sealed partial class PageToolsNeuron(
    [PersistentState("playwright.page-tools", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<PageToolsStore> store)
    : Neuron, IPageTools
{
    private const int RenderingBudgetMs = 15_000;

    private static readonly ToolDescriptor[] Descriptors =
    [
        new("commands", "List commands", "List the app's commands with their ids, labels and parameter docs; filter narrows by id substring.",
            """{"type":"object","properties":{"filter":{"type":"string"}}}""", true),
        new("execute", "Execute a command", "Run one app command by id with its parameters object, exactly as a menu or button would.",
            """{"type":"object","properties":{"command":{"type":"string"},"params":{"type":"object"}},"required":["command"]}""", false),
        new("request", "Control request", "Call a control method of the app (ui.inspect, ui.click, ui.key, ui.menu.invoke, ...).",
            """{"type":"object","properties":{"method":{"type":"string"},"params":{"type":"object"}},"required":["method"]}""", false),
    ];

    private string Key => this.GetPrimaryKeyString();
    private string Brain => BrainKey.BrainOf(Key);

    public async Task<PageToolsState> Configure(PageToolsSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Title);
        if (!ApiName().IsMatch(spec.Api)) { throw new ArgumentException($"'{spec.Api}' is not a JavaScript identifier.", nameof(spec)); }
        if (!spec.Driver.StartsWith(Brain + "/", StringComparison.Ordinal))
        { throw new UnauthorizedAccessException($"The driver '{spec.Driver}' is not in brain '{Brain}'."); }
        var state = new PageToolsState(spec.Title, spec.Driver, spec.Api, true);
        store.State = new() { State = state };
        await store.WriteStateAsync(ct);
        return state;
    }

    public Task<PageToolsState> Read() => Task.FromResult(store.State?.State ?? new PageToolsState("", "", "", false));

    // Unconfigured page tools offer none.
    public Task<ToolSet> Tools(CancellationToken ct = default)
        => Task.FromResult(store.State?.State is { Configured: true } state ? new ToolSet(state.Title, [.. Descriptors]) : new ToolSet("", []));

    public async Task<ToolResult> Run(string tool, string arguments, CancellationToken ct = default)
    {
        var state = store.State?.State is { Configured: true } configured ? configured
            : throw new InvalidOperationException("The page tools are not configured.");
        using var document = Arguments(arguments);
        var input = document.RootElement;
        var call = tool switch
        {
            "commands" => $"window.{state.Api}.request('engine.commands', {{}})",
            "execute" => $"window.{state.Api}.execute({Json(Required(input, "command"))}, {Params(input)})",
            "request" => $"window.{state.Api}.request({Json(Required(input, "method"))}, {Params(input)})",
            _ => throw new ArgumentException($"Unknown page tool '{tool}'.", nameof(tool)),
        };
        // The page answers from its frame loop: a hidden page never settles, so bound it inside the page.
        var expression = $"Promise.race([{call}, new Promise((_, reject) => setTimeout(() => reject(new Error('page not rendering')), {RenderingBudgetMs}))])";
        string json;
        try { json = await GrainFactory.GetGrain<IPlaywright>(state.Driver).Evaluate(expression, ct); }
        catch (Exception error) when (error.Message.Contains("page not rendering", StringComparison.Ordinal))
        { return new ToolResult(true, "page not rendering: open the app's window", []); }
        if (tool == "commands" && input.TryGetProperty("filter", out var filter) && filter.GetString() is { Length: > 0 } text)
        { json = Filter(json, text); }
        return new ToolResult(false, json, []);
    }

    private static JsonDocument Arguments(string arguments)
    {
        try
        {
            var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object) { return document; }
            document.Dispose();
        }
        catch (JsonException) { }
        throw new ArgumentException("arguments must be a JSON object.", nameof(arguments));
    }

    private static string Required(JsonElement input, string name)
        => input.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text
            : throw new ArgumentException($"'{name}' is required.");

    private static string Params(JsonElement input)
        => input.TryGetProperty("params", out var value) && value.ValueKind == JsonValueKind.Object ? value.GetRawText() : "{}";

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Filter(string json, string text)
    {
        using var document = JsonDocument.Parse(json);
        var list = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
            : document.RootElement.TryGetProperty("commands", out var commands) ? commands : default;
        if (list.ValueKind != JsonValueKind.Array) { return json; }
        return JsonSerializer.Serialize(list.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out var id) && id.GetString()?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray());
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ApiName();
}
