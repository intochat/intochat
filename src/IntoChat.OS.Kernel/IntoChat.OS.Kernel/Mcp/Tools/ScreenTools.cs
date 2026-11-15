using System.ComponentModel;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.OS.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IntoChat.OS.Mcp;

/// <summary>
/// First-class MCP screen tools backed by <see cref="IScreenDirectory"/> so any MCP client
/// can discover open/available screens, inspect UI trees and controls, click/submit controls,
/// and resolve pending approval gates without manual grain-key choreography.
/// </summary>
[McpServerToolType]
internal sealed class ScreenTools(INeuronProxies brain, McpCaller caller, Orleans.IGrainFactory? grains = null)
{
    private const int MaxMappedErrorLength = 500;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private IScreenDirectory Directory() => brain.Get<IScreenDirectory>(IScreenDirectory.Key(caller.Stamp().BrainId));

    [McpServerTool(Name = "screen_list"), Description("List all available screens (built-in apps, open workspace surfaces, confirmations, and deployed runtime units) with pending approval counts.")]
    public async Task<CallToolResult> List(CancellationToken cancellationToken = default)
    {
        try
        {
            var screens = await Directory().List().WaitAsync(cancellationToken).ConfigureAwait(false);
            return Text(JsonSerializer.Serialize(screens, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            return Failure(error);
        }
    }

    [McpServerTool(Name = "screen_read"), Description("Read the current rendered UI view, flat interactive controls list, generation token, and pending approvals for an app or runtime unit screen.")]
    public async Task<CallToolResult> Read(
        [Description("App id or runtime unit alias (for example 'creator', 'settings', 'datastudio', 'tasks', 'ino', 'confirmations', or a deployed unit alias).")] string appId,
        [Description("Optional workspace instance id (defaults to 'main').")] string instanceId = "main",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await Directory().Read(appId, instanceId).WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Screen '{appId}' was not found.");
            if (snapshot is { Kind: "unit", Generation: 0L, PendingApprovals.Length: 0 })
            {
                await CatchUpRuntimeUnitAsync(grains, caller.Stamp().BrainId, snapshot.AppId).WaitAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                snapshot = await Directory().Read(appId, instanceId).WaitAsync(cancellationToken).ConfigureAwait(false) ?? snapshot;
            }

            return Text(JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            return Failure(error);
        }
    }

    [McpServerTool(Name = "screen_act"), Description("Trigger an interactive control (button click, field submit/change, navigation item) on a screen by control key and optional value, returning the updated screen snapshot.")]
    public async Task<CallToolResult> Act(
        [Description("App id or runtime unit alias.")] string appId,
        [Description("Control key from screen_read's controls array.")] string key,
        [Description("Optional field input value or action argument.")] string? value = null,
        [Description("Optional optimistic view generation token from screen_read; omitted uses the current generation.")] long? generation = null,
        [Description("Optional workspace instance id (defaults to 'main').")] string instanceId = "main",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Directory().Act(appId, key, value, generation, instanceId).WaitAsync(cancellationToken).ConfigureAwait(false);
            return Text(JsonSerializer.Serialize(result, Json));
        }
        catch (Exception error) when (Mapped(error))
        {
            return Failure(error);
        }
    }

    [McpServerTool(Name = "screen_approve"), Description("Approve or deny a pending approval gate (ChangeFlow promotion, unit deploy, synapse staged install, or confirmation card) by approvalId.")]
    public async Task<CallToolResult> Approve(
        [Description("Pending approval id / plan id from screen_read or screen_list.")] string approvalId,
        [Description("True to approve and execute; false to deny/reject.")] bool approved,
        [Description("Optional reason recorded with the resolution.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var stamped = caller.Stamp();
            var inbox = brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(stamped.BrainId));
            var current = await inbox.Read().WaitAsync(cancellationToken).ConfigureAwait(false);
            var wasPendingInInbox = current.Pending.Any(p => string.Equals(p.PlanId, approvalId, StringComparison.Ordinal));
            if (wasPendingInInbox)
            {
                // Resolve directly from the authenticated edge so RequiresPerson checks observe Direct == true.
                await inbox.Resolve(approvalId, approved).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var requests = brain.Get<IConfirmationRequests>(IConfirmationRequests.Of(new BrainId(stamped.BrainId)));
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
                        new NeuronId(new BrainId(stamped.BrainId), "synapse.deployment", unitAlias));
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

            var result = await Directory().Approve(approvalId, approved, reason).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Accepted && approved && unitAlias is not null)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    await CatchUpRuntimeUnitAsync(grains, stamped.BrainId, unitAlias).WaitAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    var updated = await Directory().Read(unitAlias).WaitAsync(cancellationToken).ConfigureAwait(false);
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
            return Failure(error);
        }
    }

    private static async Task CatchUpRuntimeUnitAsync(Orleans.IGrainFactory? grains, string brainId, string alias)
    {
        if (grains is null) { return; }
        var hostType = typeof(DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext).Assembly
            .GetType("DigitalBrain.Runtime.Orleans.Synapses.ISynapseHost");
        if (hostType is null) { return; }
        var getGrain = typeof(Orleans.IGrainFactory).GetMethods()
            .FirstOrDefault(m => m.Name == nameof(Orleans.IGrainFactory.GetGrain)
                && m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 1 } p
                && p[0].ParameterType == typeof(Orleans.Runtime.GrainId));
        if (getGrain is null) { return; }
        var grain = getGrain.MakeGenericMethod(hostType)
            .Invoke(grains, [Orleans.Runtime.GrainId.Create("neuron.host", $"{brainId}/{alias}/default")]);
        var catchUp = hostType.GetMethod("CatchUp", Type.EmptyTypes);
        if (grain is not null && catchUp?.Invoke(grain, null) is Task task)
        {
            await task.ConfigureAwait(false);
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
