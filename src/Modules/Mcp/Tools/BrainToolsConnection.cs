using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Tools;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Tools;

namespace IntoChat.OS.Modules.Mcp;

[GenerateSerializer, Alias("brain.tools-connection-state")]
public sealed record BrainToolsConnectionState : ConnectionState
{
    [Id(0)] public string[] RegisteredToolNames { get; set; } = [];
}

// Brain-scoped aggregator connection for all tool@1 capabilities in the brain.
[Neuron(IBrainToolsConnection.TypeName, State = "brain.tools")]
public sealed class BrainToolsConnection(
    IServiceProvider? services = null,
    IEnumerable<IToolSource>? staticSources = null)
    : ConnectionNeuron<BrainToolsConnectionState>, IBrainToolsConnection
{
    private readonly List<IToolSource> _sources = staticSources?.ToList() ?? [];

    public void RegisterSource(IToolSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_sources.Contains(source))
        {
            _sources.Add(source);
        }
    }

    public async Task<IReadOnlyList<string>> List()
    {
        var descriptors = await DescribeAll().ConfigureAwait(false);
        return descriptors.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<ToolDescriptor>> DescribeAll(CancellationToken ct = default)
    {
        var allTools = new List<ToolDescriptor>();
        var sources = await GetSourcesAsync().ConfigureAwait(false);

        foreach (var source in sources)
        {
            try
            {
                var toolSet = await source.Tools(ct).ConfigureAwait(false);
                if (toolSet?.Tools is not null)
                {
                    allTools.AddRange(toolSet.Tools);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failing source should not crash tool discovery
            }
        }

        return allTools;
    }

    public async Task<IReadOnlyList<ToolDescriptor>> Search(string query, CancellationToken ct = default)
    {
        var all = await DescribeAll(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query))
        {
            return all;
        }

        var trimmed = query.Trim();
        return all.Where(t =>
            t.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
            t.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<bool> Validate(string tool, JsonElement args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tool)) { return false; }
        var all = await DescribeAll(ct).ConfigureAwait(false);
        var descriptor = all.FirstOrDefault(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null) { return false; }

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return false;
        }

        return true;
    }

    public async Task<JsonElement> Run(string tool, JsonElement args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new ToolExecutionException(tool, "Tool arguments must be a JSON object.", isValidationError: true);
        }

        var sources = await GetSourcesAsync().ConfigureAwait(false);
        IToolSource? matchedSource = null;

        foreach (var source in sources)
        {
            try
            {
                var toolSet = await source.Tools(ct).ConfigureAwait(false);
                if (toolSet?.Tools?.Any(t => string.Equals(t.Name, tool.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
                {
                    matchedSource = source;
                    break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Skipping faulty tool source during lookup.");
            }
        }

        if (matchedSource is null)
        {
            throw new ToolExecutionException(tool, $"Tool '{tool}' was not found in any available tool source in brain '{Id.Brain.Value}'.", isNotFound: true);
        }

        string argumentsString = args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : args.GetRawText();
        ToolResult result;
        try
        {
            result = await matchedSource.Run(tool, argumentsString, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolExecutionException)
        {
            throw new ToolExecutionException(tool, ex.Message, ex);
        }

        if (result.IsError)
        {
            throw new ToolExecutionException(tool, result.Text);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Text);
            return doc.RootElement.Clone();
        }
        catch (JsonException error)
        {
            global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Wrapping non-JSON tool result text.");
            return JsonSerializer.SerializeToElement(new { result = result.Text });
        }
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct)
    {
        return Task.FromResult(ConnectionProbeResult.Connected("Brain tools aggregator ready."));
    }

    private async Task<IReadOnlyList<IToolSource>> GetSourcesAsync()
    {
        var result = new List<IToolSource>(_sources);

        // In a live brain, discover plain neuron tool sources via IBrainObserver + SiloNeurons
        var sp = services ?? ServiceProvider;
        if (sp is not null && !string.IsNullOrWhiteSpace(Id.Brain.Value))
        {
            try
            {
                if (sp.GetService(typeof(DigitalBrain.Kernel.IBrainObserver)) is DigitalBrain.Kernel.IBrainObserver observer)
                {
                    foreach (var source in (await observer.Types(CancellationToken.None).ConfigureAwait(false))
                        .Where(type => type.Kind == DigitalBrain.Kernel.TypeKind.Neuron))
                    {
                        var contract = AppDomain.CurrentDomain.GetAssemblies()
                            .Where(assembly => !assembly.IsDynamic)
                            .Select(assembly => assembly.GetType(source.Name, throwOnError: false))
                            .FirstOrDefault(type => type is { IsInterface: true }
                                && typeof(IToolSource).IsAssignableFrom(type)
                                && DigitalBrain.Contracts.PlainNeuronContracts.Is(type));
                        if (contract is null)
                        {
                            continue;
                        }

                        await foreach (var instance in observer.Instances(source.Alias, CancellationToken.None).ConfigureAwait(false))
                        {
                            try
                            {
                                var liveSource = (IToolSource)DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons.Get(sp, contract, instance.Key);
                                if (!result.Contains(liveSource))
                                {
                                    result.Add(liveSource);
                                }
                            }
                            catch (Exception innerError) when (innerError is not OperationCanceledException)
                            {
                                global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(innerError, "Skipping unavailable live tool source.");
                            }
                        }
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Falling back to local tool sources.");
            }
        }

        return result;
    }
}
