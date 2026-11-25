using System.ComponentModel;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
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
/// can discover open/available screens, inspect UI trees and controls, click/submit controls,
/// and resolve pending approval gates without manual grain-key choreography.
/// </summary>
[McpServerToolType]
internal sealed class ScreenTools(INeuronProxies brain, McpCaller caller)
{
    private const int MaxMappedErrorLength = 500;
    private static readonly JsonSerializerOptions Json = KernelMcpJsonContext.Default.Options;

    private const string ListName = "screen_list";
    private const string ReadName = "screen_read";
    private const string ActName = "screen_act";
    private const string ApproveName = "screen_approve";

    private IScreenDirectory Directory(string tool) => brain.Get<IScreenDirectory>(IScreenDirectory.Key(caller.Stamp(tool).Brain.Value));

    // Only a person calling directly, outside a conversation, answers a confirmation card (the inbox's own Answerer rule).
    internal static bool MayAnswerConfirmation(DigitalBrain.Access.Caller caller)
        => caller is { Direct: true, Conversation: null, Actor: DigitalBrain.Access.Actor.Person };

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

    [McpServerTool(Name = ReadName), Description("Read the current rendered UI view, flat interactive controls list, generation token, and pending approvals for an app or runtime unit screen.")]
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

    [McpServerTool(Name = ApproveName), Description("Approve or deny a pending approval gate (FeatureToPrFlow promotion, unit deploy, synapse staged install, or confirmation card) by approvalId.")]
    public async Task<CallToolResult> Approve(
        [Description("Pending approval id / plan id from screen_read or screen_list.")] string approvalId,
        [Description("True to approve and execute; false to deny/reject.")] bool approved,
        [Description("Optional reason recorded with the resolution.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stamped = caller.Stamp(ApproveName);
            var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(stamped.Brain.Value));
            var current = await inbox.Read().WaitAsync(cancellationToken).ConfigureAwait(false);
            var wasPendingInInbox = current.Pending.Any(p => string.Equals(p.PlanId, approvalId, StringComparison.Ordinal));
            if (wasPendingInInbox)
            {
                // MCP is stamped as the agent: a confirmation card is the person's to answer in the shell (owner decision 3). The
                // inbox would refuse a person-only plan and record any other answer from an agent as a self-approval denial.
                if (!MayAnswerConfirmation(stamped))
                {
                    return Text(JsonSerializer.Serialize(
                        new ScreenActionResult(false, $"Approval '{approvalId}' is a confirmation; the person answers it in the shell."),
                        Json));
                }
                await inbox.Resolve(approvalId, approved).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var requests = brain.Get<IConfirmationRequests>(IConfirmationRequests.Of(stamped.Brain));
                if (await requests.Outcome(approvalId).WaitAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    return Text(JsonSerializer.Serialize(
                        new ScreenActionResult(false, $"Approval '{approvalId}' has already been resolved."),
                        Json));
                }
            }

            string? unitAlias = null;
            if (approved && approvalId.StartsWith("install:", StringComparison.Ordinal))
            {
                var parts = approvalId.Split(':');
                if (parts.Length >= 2)
                {
                    unitAlias = parts[1];
                    var deployment = brain.Get<DigitalBrain.Units.IUnitDeployment>(
                        new NeuronId(stamped.Brain, "synapse.deployment", unitAlias));
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        if (await deployment.Ref(DigitalBrain.Units.UnitView.Live, cancellationToken).ConfigureAwait(false) is not null)
                        {
                            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var result = await Directory(ApproveName).Approve(approvalId, approved, reason).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Accepted && approved && unitAlias is not null)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    var updated = await Directory(ApproveName).Read(unitAlias).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (updated is { Controls.Length: > 0 })
                    {
                        break;
                    }
                }
            }

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
