using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Errors;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.State;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Screens;

/// <summary>
/// Unified screen gateway neuron for listing, reading, acting on, and approving UI across built-in apps and runtime units.
/// </summary>
[Neuron(IScreenDirectory.TypeName, State = "screens")]
internal sealed partial class ScreenDirectoryNeuron(
    INeuronProxies? proxies = null,
    ILogger<ScreenDirectoryNeuron>? logger = null)
    : UiNeuron<ScreenDirectoryState>, IScreenDirectory
{
    private readonly string? _brainOverride;
    private ScreenDirectoryState? _inMemoryState;

    private ILogger Log => logger ?? ShellConsoleLog.For<ScreenDirectoryNeuron>();

    internal ScreenDirectoryNeuron(INeuronProxies? proxies, string? brainOverride, ILogger<ScreenDirectoryNeuron>? logger = null)
        : this(proxies, logger)
        => _brainOverride = brainOverride;

    private string Brain => _brainOverride ?? Id.Brain.Value;

    private ScreenDirectoryState CurrentState => _brainOverride is not null ? (_inMemoryState ??= new()) : Snapshot;

    private Task PersistState(ScreenDirectoryState next, params DigitalBrain.Signal[] signals)
    {
        if (_brainOverride is not null)
        {
            _inMemoryState = next;
            return Task.CompletedTask;
        }
        return Save(next, signals);
    }

    private T ResolveNeuron<T>(NeuronId id) where T : class => proxies?.Get<T>(id) ?? Get<T>(id);
    private T ResolveNeuron<T>(string key) where T : class => ResolveNeuron<T>(PlainNeuronContracts.IdOf(typeof(T), key));

    [DigitalBrain.Contracts.ReadOnlyCall]
    public async Task<ScreenSummary[]> List()
    {
        var brainId = Brain;
        var summaries = new Dictionary<string, ScreenSummary>(StringComparer.OrdinalIgnoreCase);

        // 1. Read Workspace
        WorkspaceState? ws = null;
        try
        {
            ws = await ResolveNeuron<IWorkspace>(IWorkspace.Key(brainId)).Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, "Workspace could not be read for screen directory.");
        }

        // 2. Read Pending Approvals count by app / subject
        var pendingApprovalsByApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var approvalsNeuron = ResolveNeuron<IApprovals>(IApprovals.Key(brainId));
            var pendingList = await approvalsNeuron.Pending();
            foreach (var app in pendingList)
            {
                var targetApp = string.IsNullOrEmpty(app.Subject) ? "confirmations" : app.Subject;
                pendingApprovalsByApp[targetApp] = pendingApprovalsByApp.GetValueOrDefault(targetApp) + 1;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, "IApprovals could not be read for screen directory.");
            try
            {
                var inbox = await ResolveNeuron<IConfirmationInbox>(IConfirmationInbox.Key(brainId)).Read();
                if (inbox.Pending.Length > 0)
                {
                    pendingApprovalsByApp["confirmations"] = inbox.Pending.Length;
                }
            }
            catch (Exception inboxEx) when (inboxEx is not OperationCanceledException)
            {
                Log.LogSwallowed(inboxEx, "IConfirmationInbox could not be read for screen directory.");
            }
        }

        // 3. Process open views from workspace
        if (ws is not null)
        {
            foreach (var view in ws.OpenViews ?? [])
            {
                if (string.IsNullOrWhiteSpace(view.Id)) { continue; }
                var appId = NormalizeAppId(brainId, view.App);
                var isFocused = string.Equals(view.Id, ws.FocusedViewId, StringComparison.Ordinal)
                    || string.Equals(appId, ws.FocusedViewId, StringComparison.OrdinalIgnoreCase);
                var pendingCount = pendingApprovalsByApp.GetValueOrDefault(appId, 0);

                var title = FormatTitle(appId);
                summaries[appId] = new ScreenSummary(
                    AppId: appId,
                    Title: title,
                    Kind: view.Variant.Length > 0 ? view.Variant : "app",
                    OpenInWorkspace: true,
                    Focused: isFocused,
                    PendingApprovals: pendingCount,
                    InstanceId: view.Id);
            }
        }

        // 4. Derive shipped and installed apps from the brain's app catalog (IAppStore),
        // falling back to core shipped app ids only in minimal test hosts without IAppStore.
        IReadOnlyList<GalleryEntry>? catalogEntries = null;
        try
        {
            catalogEntries = await ResolveNeuron<IAppStore>(IAppStore.Key(brainId)).List();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, "IAppStore could not be read for screen directory; falling back to core apps.");
        }

        if (catalogEntries is { Count: > 0 })
        {
            foreach (var entry in catalogEntries)
            {
                if (!string.Equals(entry.Kind, "app", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }
                var appId = NormalizeAppId(brainId, entry.InstalledName ?? entry.Name);
                if (!summaries.ContainsKey(appId))
                {
                    var pendingCount = pendingApprovalsByApp.GetValueOrDefault(appId, 0);
                    summaries[appId] = new ScreenSummary(
                        AppId: appId,
                        Title: string.IsNullOrWhiteSpace(entry.Title) ? FormatTitle(appId) : entry.Title,
                        Kind: "app",
                        OpenInWorkspace: false,
                        Focused: false,
                        PendingApprovals: pendingCount,
                        InstanceId: "main");
                }
            }
        }
        else
        {
            foreach (var standardApp in new[] { "ino", "gallery", "calculator", "creator", "engineer", "aspire", "orleans", "photocraft", "data", "tasks", "settings", "files", "uikit" })
            {
                if (!summaries.ContainsKey(standardApp))
                {
                    var pendingCount = pendingApprovalsByApp.GetValueOrDefault(standardApp, 0);
                    summaries[standardApp] = new ScreenSummary(
                        AppId: standardApp,
                        Title: FormatTitle(standardApp),
                        Kind: "app",
                        OpenInWorkspace: false,
                        Focused: false,
                        PendingApprovals: pendingCount,
                        InstanceId: "main");
                }
            }
        }

        if (!summaries.ContainsKey("confirmations"))
        {
            var confPending = pendingApprovalsByApp.Values.Sum();
            summaries["confirmations"] = new ScreenSummary(
                AppId: "confirmations",
                Title: "Confirmations",
                Kind: "surface",
                OpenInWorkspace: false,
                Focused: false,
                PendingApprovals: confPending,
                InstanceId: "main");
        }

        return [.. summaries.Values];
    }

    [DigitalBrain.Contracts.ReadOnlyCall]
    public async Task<ScreenSnapshot?> Read(string appId, string instanceId = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var brainId = Brain;
        var normalizedApp = NormalizeAppId(brainId, appId);

        long generation = 1L;
        string resolvedKind = "app";
        View? viewObject = null;
        JsonElement? viewJson = null;

        if (normalizedApp == "confirmations")
        {
            try
            {
                var inbox = await ResolveNeuron<IConfirmationInbox>(IConfirmationInbox.Key(brainId)).Read();
                viewObject = ConfirmationView.Render(inbox, Context.Caller);
                generation = 1L;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "ConfirmationView render failed.");
            }
        }

        if (viewObject is null && (!normalizedApp.Contains('.', StringComparison.Ordinal) || normalizedApp.StartsWith("app.", StringComparison.Ordinal)))
        {
            // Try IScreen: app.{normalizedApp}
            try
            {
                var screenAlias = normalizedApp.StartsWith("app.", StringComparison.Ordinal) ? normalizedApp : "app." + normalizedApp;
                var screenId = new NeuronId(new BrainId(brainId), screenAlias, "default");
                var screen = ResolveNeuron<IScreen>(screenId);
                var pub = await screen.Read();
                if (pub is null)
                {
                    var appPrefix = screenAlias.StartsWith("app.", StringComparison.Ordinal) ? screenAlias[4..] : screenAlias;
                    await screen.Request($"{appPrefix}.open", JsonSerializer.SerializeToElement(new { }));
                    pub = await screen.Read();
                }
                if (pub is not null)
                {
                    generation = pub.Generation;
                    viewJson = SlotKeys.Hide(await InlineSlotsAsync(pub.View));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IScreen read failed.");
            }
        }

        if (viewObject is null && viewJson is null)
        {
            // Try IView: {brainId}/apps/{normalizedApp}/view or appId
            try
            {
                var viewName = appId.Contains('/') ? appId : IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), normalizedApp).ViewKey;
                var pub = await ResolveNeuron<IView>(viewName).Read();
                if (pub is not null)
                {
                    generation = pub.Generation;
                    viewJson = SlotKeys.Hide(pub.View);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IView read failed.");
            }
        }

        if (viewObject is null && viewJson is null)
        {
            // Try runtime unit IViewReader at {brainId}/{normalizedApp}/default
            try
            {
                var unitNeuronId = new NeuronId(new BrainId(brainId), normalizedApp, "default");
                var unitView = await ResolveNeuron<IViewReader>(unitNeuronId).Read(normalizedApp);
                if (unitView is not null)
                {
                    generation = unitView.Generation;
                    viewJson = SlotKeys.Hide(unitView.View);
                    resolvedKind = "unit";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IViewReader read failed.");
            }
        }

        if (viewObject is null && viewJson is null)
        {
            try
            {
                var installs = ResolveNeuron<IInstalls>(new NeuronId(new BrainId(brainId), "synapse.installs", "default"));
                var isPending = (await installs.Pending(CancellationToken.None)).Any(p => string.Equals(p.Alias, normalizedApp, StringComparison.Ordinal));
                var isInstalled = isPending || await installs.Describe(normalizedApp, CancellationToken.None) is not null;
                if (isInstalled)
                {
                    var unitApprovals = await ExtractApprovals(brainId, normalizedApp, null);
                    return new ScreenSnapshot(
                        AppId: normalizedApp,
                        InstanceId: instanceId,
                        Title: FormatTitle(normalizedApp),
                        Kind: "unit",
                        Generation: 0L,
                        View: null,
                        Controls: [],
                        PendingApprovals: unitApprovals);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IInstalls check for runtime unit failed.");
            }
        }

        if (viewObject is not null && viewJson is null)
        {
            try
            {
                var serialized = ViewValidation.Serialize(viewObject);
                viewJson = ViewValidation.Parse(serialized);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "View serialization failed.");
            }
        }

        if (viewObject is null && viewJson is null)
        {
            // Fallback: check if IApp exists and auto-open it on first screen read
            try
            {
                var appKey = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), normalizedApp).Key;
                var appNeuron = ResolveNeuron<IApp>(appKey);
                var app = await appNeuron.Read();
                if (app is not null && !string.IsNullOrWhiteSpace(app.Manifest.Title))
                {
                    try
                    {
                        await appNeuron.Request("open", "{}");
                        var viewName = appId.Contains('/') ? appId : IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), normalizedApp).ViewKey;
                        var pub = await ResolveNeuron<IView>(viewName).Read();
                        if (pub is not null)
                        {
                            generation = pub.Generation;
                            viewJson = SlotKeys.Hide(pub.View);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.LogSwallowed(ex, "IApp auto-open failed.");
                    }

                    if (viewJson is null)
                    {
                        return new ScreenSnapshot(
                            AppId: normalizedApp,
                            InstanceId: instanceId,
                            Title: app.Manifest.Title,
                            Kind: "app",
                            Generation: 0L,
                            View: null,
                            Controls: [],
                            PendingApprovals: []);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IApp read failed.");
            }
            if (viewJson is null)
            {
                return null;
            }
        }

        if (viewObject is null && viewJson is { } rawView)
        {
            try
            {
                viewObject = rawView.Deserialize<View>(DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "View deserialization failed.");
            }
        }

        var controls = ExtractControls(viewJson);
        var pendingApprovals = await ExtractApprovals(brainId, normalizedApp, viewJson);

        return new ScreenSnapshot(
            AppId: normalizedApp,
            InstanceId: instanceId,
            Title: FormatTitle(normalizedApp),
            Kind: resolvedKind,
            Generation: generation,
            View: viewObject,
            Controls: controls,
            PendingApprovals: pendingApprovals);
    }

    public async Task<ScreenActionResult> Act(string appId, string key, string? value = null, long? generation = null, string instanceId = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var brainId = Brain;
        var normalizedApp = NormalizeAppId(brainId, appId);

        var current = await Read(appId, instanceId);
        if (current is null)
        {
            return new ScreenActionResult(false, $"Screen for app '{appId}' not found.");
        }

        var targetGen = generation ?? current.Generation;

        // Special case: confirmations screen resolution
        if (normalizedApp == "confirmations" && value is "approve" or "deny")
        {
            var approveResult = await Approve(key, value == "approve");
            var updatedSnapshot = await Read(appId, instanceId);
            return new ScreenActionResult(approveResult.Accepted, approveResult.Message, updatedSnapshot);
        }

        // Try IScreen: app.{normalizedApp}
        if (!normalizedApp.Contains('.', StringComparison.Ordinal) || normalizedApp.StartsWith("app.", StringComparison.Ordinal))
        {
            try
            {
                var screenAlias = normalizedApp.StartsWith("app.", StringComparison.Ordinal) ? normalizedApp : "app." + normalizedApp;
                var screenId = new NeuronId(new BrainId(brainId), screenAlias, "default");
                var screen = ResolveNeuron<IScreen>(screenId);
                var pub = await screen.Read();
                if (pub is not null)
                {
                    var callerPersonId = Context.Caller?.For?.Principal is { Length: > 0 } person ? person : "user";
                    // A key path "slot/key" (as Read lists a slot child's controls) activates the key on that slot's own view.
                    if (await SlotTargetAsync(pub.View, key) is { } slot)
                    {
                        var child = ResolveNeuron<IScreen>(slot.Child);
                        var childView = await child.Read() ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownChild);
                        await child.Activate(childView.Alias, childView.Generation, slot.Key, value, callerPersonId);
                        return new ScreenActionResult(true, "Action accepted.", await Read(appId, instanceId));
                    }
                    await ScreenAppEmitBridge.ActivateAsync(
                        screen,
                        pub,
                        screenAlias,
                        targetGen,
                        key,
                        value,
                        callerPersonId,
                        logger: Log);
                    var updated = await Read(appId, instanceId);
                    return new ScreenActionResult(true, "Action accepted.", updated);
                }
            }
            catch (UnauthorizedAccessException unauth)
            {
                return new ScreenActionResult(false, unauth.Message);
            }
            catch (ActivationRefusedException refused)
            {
                return new ScreenActionResult(false, $"Action refused: {refused.Reason}");
            }
            catch (StaleActionException stale)
            {
                return new ScreenActionResult(false, stale.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IScreen activation fallback.");
            }
        }

        // Try IView (skip when resolved as a runtime unit, and only activate when published)
        if (!string.Equals(current.Kind, "unit", StringComparison.Ordinal))
        {
            try
            {
                var viewName = appId.Contains('/') ? appId : IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), normalizedApp).ViewKey;
                var viewNeuron = ResolveNeuron<IView>(viewName);
                if (await viewNeuron.Read() is not null)
                {
                    var activation = await viewNeuron.Activate(targetGen, key, value);

                    switch (activation.Kind)
                    {
                        case ViewActivationKind.Emit:
                            var emit = activation.Emit!;
                            var appKey = IntoChat.OS.Host.Primitives.AppRef.TryParse(new BrainId(brainId), emit.Owner, out var emitRef)
                                ? emitRef.Key
                                : IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), emit.Owner).Key;
                            var args = value is null ? emit.Args : ViewArgs.WithValue(emit.Args, value);
                            await ResolveNeuron<IApp>(appKey).Emit(emit.Signal, args?.GetRawText() ?? "");
                            break;
                        case ViewActivationKind.Resolve:
                            throw new UnauthorizedAccessException("Only the OS confirmation inbox can answer a confirmation.");
                        case ViewActivationKind.Intent:
                            break;
                        case ViewActivationKind.Command:
                            // A slotless app: a command to a child address has nowhere to go.
                            return new ScreenActionResult(false, $"{ActivationRefusedException.NoSlots}: {appId} has no child slots, so the command to '{activation.Command!.Target}' has nowhere to go.");
                    }

                    var updated = await Read(appId, instanceId);
                    return new ScreenActionResult(true, "Action accepted.", updated);
                }
            }
            catch (UnauthorizedAccessException unauth)
            {
                return new ScreenActionResult(false, unauth.Message);
            }
            catch (StaleActionException stale)
            {
                return new ScreenActionResult(false, stale.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IView activation fallback.");
            }
        }

        // Try runtime unit IViewReader
        try
        {
            var unitNeuronId = new NeuronId(new BrainId(brainId), normalizedApp, "default");
            var reader = ResolveNeuron<IViewReader>(unitNeuronId);
            var callerPersonId = Context.Caller?.For?.Principal is { Length: > 0 } person ? person : "user";
            await reader.Activate(normalizedApp, targetGen, key, value, callerPersonId);
            var updated = await Read(appId, instanceId);
            return new ScreenActionResult(true, "Action accepted.", updated);
        }
        catch (ActivationRefusedException refused)
        {
            return new ScreenActionResult(false, $"Action refused: {refused.Reason}");
        }
        catch (StaleActionException stale)
        {
            return new ScreenActionResult(false, stale.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, $"runtime unit view activation failed for '{appId}' control '{key}'");
            return new ScreenActionResult(false, ex.Message);
        }
    }

    public async Task<ScreenActionResult> Approve(string approvalId, bool approved, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);

        if (CurrentState.ResolvedApprovals.Contains(approvalId, StringComparer.Ordinal))
        {
            return new ScreenActionResult(false, $"Approval '{approvalId}' has already been resolved.");
        }

        var brainId = Brain;
        var inbox = ResolveNeuron<IConfirmationInbox>(IConfirmationInbox.Key(brainId));

        if (_brainOverride is not null)
        {
            try
            {
                await inbox.Resolve(approvalId, approved);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "IConfirmationInbox.Resolve failed in ScreenDirectoryNeuron.");
            }
        }
        else
        {
            ConfirmationResult? outcome = null;
            var wasPending = false;
            try
            {
                var before = await inbox.Read();
                wasPending = before.Pending.Any(p => string.Equals(p.PlanId, approvalId, StringComparison.Ordinal));
                if (wasPending)
                {
                    await inbox.Resolve(approvalId, approved);
                }

                var requests = ResolveNeuron<IConfirmationRequests>(IConfirmationRequests.Of(new BrainId(brainId)));
                outcome = await requests.Outcome(approvalId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, "Approval resolution or outcome verification failed in ScreenDirectoryNeuron.");
                return new ScreenActionResult(false, $"Approval '{approvalId}' could not be resolved: {ex.Message}");
            }

            if (!wasPending)
            {
                return outcome is not null
                    ? new ScreenActionResult(false, $"Approval '{approvalId}' has already been resolved.")
                    : new ScreenActionResult(false, $"Approval '{approvalId}' was not found or could not be resolved.");
            }

            if (outcome is null || outcome.Approved != approved)
            {
                return new ScreenActionResult(false, $"Approval '{approvalId}' was not found or could not be resolved.");
            }
        }

        string[] nextResolved = [.. CurrentState.ResolvedApprovals, approvalId];
        await PersistState(CurrentState with { ResolvedApprovals = nextResolved }, new ApprovalResolved(brainId, approvalId, approved));

        return new ScreenActionResult(true, $"Approval '{approvalId}' resolved as {(approved ? "approved" : "denied")}.");
    }
}
