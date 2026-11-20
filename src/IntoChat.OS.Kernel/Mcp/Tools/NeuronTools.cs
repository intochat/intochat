using System.ComponentModel;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http.Surface;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Host.Kernel;

namespace IntoChat.OS.Kernel.Mcp.Tools;

[McpServerToolType]
internal sealed class NeuronTools(OsNeuronSurface surface, McpCaller caller)
{
    private const int MaxMappedErrorLength = 500;
    [McpServerTool(Name = NeuronSurfaceTools.ListName), Description(NeuronSurfaceTools.ListDescription)]
    public Task<IReadOnlyList<NeuronInstance>> List([Description("Optional full contract type name to list one type only.")] string? contract = null, CancellationToken cancellationToken = default)
    {
        caller.Stamp(NeuronSurfaceTools.ListName);
        return surface.List(contract, cancellationToken);
    }

    [McpServerTool(Name = NeuronSurfaceTools.SearchName), Description(NeuronSurfaceTools.SearchDescription)]
    public Task<IReadOnlyList<NeuronTypeHit>> Search(string query, CancellationToken cancellationToken = default)
    {
        caller.Stamp(NeuronSurfaceTools.SearchName);
        return surface.Search(query, cancellationToken);
    }

    // A failure the caller can act on returns as an isError result: "{Type}: {Message}"
    // (clipped, never a stack trace). Anything else stays the generic MCP error.
    [McpServerTool(Name = NeuronSurfaceTools.GetName), Description(NeuronSurfaceTools.GetDescription)]
    public async Task<CallToolResult> Get(string contract, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            caller.Stamp(NeuronSurfaceTools.GetName);
            return Text(await surface.Get(contract, key, cancellationToken));
        }
        catch (Exception error) when (Mapped(error))
        {
            OsConsoleLog.For<NeuronTools>().LogSwallowed(error, "MCP neuron tool invocation failed.");
            return Failure(error);
        }
    }

    [McpServerTool(Name = NeuronSurfaceTools.CallName), Description(NeuronSurfaceTools.CallDescription)]
    public async Task<CallToolResult> Call(string contract, string key, string method, string arguments = "[]", CancellationToken cancellationToken = default)
    {
        try
        {
            caller.Stamp(NeuronSurfaceTools.CallName);
            var result = await surface.Call(contract, key, method, arguments, cancellationToken);
            return result is { } element ? Text(element.GetRawText()) : new CallToolResult { Content = [] };
        }
        catch (Exception error) when (Mapped(error))
        {
            OsConsoleLog.For<NeuronTools>().LogSwallowed(error, "MCP neuron tool invocation failed.");
            return Failure(error);
        }
    }

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    // A surfaced exception is one a caller can act on: an invalid argument, a missing key, a refused
    // access check, or an
    // Orleans call failure (whose inner exception is the reason the grain gives). A grain exception
    // whose type this host cannot load arrives as a TypeLoadException naming that type.
    private static bool Mapped(Exception error)
    {
        for (var cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or TypeLoadException or UnauthorizedAccessException) { return true; }
            if (cause.GetType().FullName?.StartsWith("Orleans.", StringComparison.Ordinal) == true) { return true; }
        }
        return false;
    }

    private static CallToolResult Failure(Exception error)
    {
        var cause = error;
        for (var candidate = error; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or UnauthorizedAccessException) { cause = candidate; break; }
            cause = candidate;
        }
        var text = $"{cause.GetType().Name}: {cause.Message}";
        if (text.Length > MaxMappedErrorLength) { text = text[..MaxMappedErrorLength]; }
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text }] };
    }
}
