using System.ComponentModel;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Kernel.Mcp.Tools;

/// <summary>
/// First-class MCP screen tools backed by <see cref="IScreenDirectory"/> so any MCP client
/// can discover open/available screens, inspect UI trees and controls, and click/submit controls without manual grain-key
/// choreography. No tool answers a confirmation: MCP is stamped as the agent, and a person answers on /confirmations/{planId}.
/// </summary>
[McpServerToolType]
internal sealed class ScreenTools(INeuronProxies brain, McpCaller caller)
{
    private const int MaxMappedErrorLength = 500;
    private static readonly JsonSerializerOptions Json = KernelMcpJsonContext.Default.Options;

    private const string ListName = "screen_list";
    private const string ReadName = "screen_read";
    private const string ActName = "screen_act";

    private IScreenDirectory Directory(string tool) => brain.Get<IScreenDirectory>(IScreenDirectory.Key(caller.Stamp(tool).Brain.Value));

    // A guest opens the shipped demo screens only, named exactly.
    private void Admit(string tool, string appId)
    {
        if (GuestSurface.Is(caller.Stamp(tool)) && !GuestSurface.MayOpen(appId))
        {
            throw new UnauthorizedAccessException($"A guest session cannot open screen '{appId}'.");
        }
    }

    [McpServerTool(Name = ListName), Description("List all available screens (built-in apps, open workspace surfaces, confirmations, and deployed runtime units) with pending approval counts.")]
    public async Task<CallToolResult> List(CancellationToken cancellationToken = default)
    {
        try
        {
            var guest = GuestSurface.Is(caller.Stamp(ListName));
            var screens = await Directory(ListName).List().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (guest) { screens = [.. screens.Where(screen => GuestSurface.MayOpen(screen.AppId))]; }
            return Text(JsonSerializer.Serialize(screens, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            OsConsoleLog.For<ScreenTools>().LogSwallowed(error, "MCP screen tool invocation failed.");
            return Failure(error);
        }
    }

    [McpServerTool(Name = ReadName), Description("Read the current rendered UI view, flat interactive controls list, generation token, and pending approvals for an app or runtime unit screen. A pending approval is the person's to answer in the shell; no tool answers it.")]
    public async Task<CallToolResult> Read(
        [Description("App id or runtime unit alias (for example 'creator', 'settings', 'datastudio', 'tasks', 'ino', 'confirmations', or a deployed unit alias).")] string appId,
        [Description("Optional workspace instance id (defaults to 'main').")] string instanceId = "main",
        CancellationToken cancellationToken = default)
    {
        try
        {
            Admit(ReadName, appId);
            var snapshot = await Directory(ReadName).Read(appId, instanceId).WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Screen '{appId}' was not found.");

            return Text(JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            OsConsoleLog.For<ScreenTools>().LogSwallowed(error, "MCP screen tool invocation failed.");
            return Failure(error);
        }
    }

    [McpServerTool(Name = ActName), Description("Send the command of an interactive control (button click, field submit, navigation item) named by its key in screen_read's controls, with an optional value, through the app's screen (a child's control through its parent), returning the updated screen snapshot.")]
    public async Task<CallToolResult> Act(
        [Description("App id or runtime unit alias.")] string appId,
        [Description("Control key from screen_read's controls array.")] string key,
        [Description("Optional field input value or action argument.")] string? value = null,
        [Description("Optional view generation from screen_read; the action is refused as stale when the screen has moved on. Omitted acts on the current view.")] long? generation = null,
        [Description("Optional workspace instance id (defaults to 'main').")] string instanceId = "main",
        CancellationToken cancellationToken = default)
    {
        try
        {
            Admit(ActName, appId);
            var result = await Directory(ActName).Act(appId, key, value, generation, instanceId).WaitAsync(cancellationToken).ConfigureAwait(false);
            return Text(JsonSerializer.Serialize(result, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            OsConsoleLog.For<ScreenTools>().LogSwallowed(error, "MCP screen tool invocation failed.");
            return Failure(error);
        }
    }

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

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
