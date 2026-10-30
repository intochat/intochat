using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chart;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Table;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Hosts compiled <see cref="DigitalBrain.App"/> classes declared in <see cref="AppsModule"/> (<see cref="InoApp"/>,
/// <see cref="GalleryApp"/>, <see cref="CreatorApp"/>, and <see cref="PostApp"/>) in-process on the silo,
/// publishing their <see cref="IView"/> trees, <see cref="Presentation"/>s, and Ino's interactive <see cref="IChat"/> + <see cref="IComposer"/>.
/// </summary>
internal sealed class CompiledAppsHost(
    IServiceProvider services,
    DigitalBrain.Spi.IClusterListeners listeners,
    ILogger<CompiledAppsHost> logger) : IStartupTask
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly (string Name, Type AppType, AppBundle Bundle)[] Hosted =
    [
        ("ino", typeof(InoApp), InoApp.Bundle),
        ("gallery", typeof(GalleryApp), GalleryApp.Bundle),
        ("creator", typeof(CreatorApp), CreatorApp.Bundle),
        ("post", typeof(PostApp), PostApp.Bundle),
    ];

    private static readonly IReadOnlyDictionary<string, (string Name, Type AppType, AppBundle Bundle)> HostedByName =
        Hosted.ToDictionary(item => item.Name, StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Brain, string Name), HostedRuntime> _runtimes = new();

    public async Task Execute(CancellationToken cancellationToken)
    {
        _ = await listeners.Listen<Activated>(nameof(CompiledAppsHost), (publisher, _, _) =>
        {
            var brain = publisher.Brain.Value;
            _ = Task.Run(() => EnsureAllAsync(brain), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<AppRequested>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var appName = publisher.Key["apps/".Length..];
            if (!HostedByName.ContainsKey(appName)) { return Task.CompletedTask; }
            _ = Task.Run(() => HandleAppRequestedAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ComposerSubmitted>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            if (publisher.Key != "apps/ino/composer") { return Task.CompletedTask; }
            _ = Task.Run(() => HandleComposerSubmittedAsync(publisher.Brain.Value, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = await listeners.Listen<ViewAction>(nameof(CompiledAppsHost), (publisher, signal, token) =>
        {
            if (!publisher.Key.StartsWith("apps/", StringComparison.Ordinal)) { return Task.CompletedTask; }
            var rest = publisher.Key["apps/".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) { return Task.CompletedTask; }
            var appName = rest[..slash];
            if (!HostedByName.ContainsKey(appName)) { return Task.CompletedTask; }
            _ = Task.Run(() => HandleViewActionAsync(publisher.Brain.Value, appName, signal, token), CancellationToken.None);
            return Task.CompletedTask;
        });

        _ = Task.Run(() => EnsureAllAsync("owner"), CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task EnsureAllAsync(string brain)
    {
        foreach (var (name, _, _) in Hosted)
        {
            await EnsureAppAsync(brain, name);
        }
    }

    private async Task EnsureAppAsync(string brain, string appName)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain)
            {
                CallerContextStamper.Stamp(caller);
            }
            var runtime = GetOrCreateRuntime(brain, appName);
            await runtime.Gate.WaitAsync();
            try
            {
                var app = SiloNeurons.Get<IApp>(services, runtime.AppKey);
                var snapshot = await app.Read();
                if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
                {
                    await app.Import(runtime.Bundle);
                    snapshot = await app.Read();
                }

                var mainView = SiloNeurons.Get<IView>(services, runtime.AppKey + "/view");
                if (!runtime.Presented || await mainView.Read() is null)
                {
                    await PublishViewsAndPresentationAsync(runtime, app, snapshot);
                }
            }
            finally
            {
                runtime.Gate.Release();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} was not initialized in {Brain}.", appName, brain);
        }
    }

    private async Task HandleAppRequestedAsync(string brain, string appName, AppRequested request, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain)
            {
                CallerContextStamper.Stamp(caller);
            }

            var runtime = GetOrCreateRuntime(brain, appName);
            await runtime.Gate.WaitAsync(ct);
            try
            {
                var app = SiloNeurons.Get<IApp>(services, runtime.AppKey);
                var snapshot = await app.Read();
                if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
                {
                    await app.Import(runtime.Bundle);
                    snapshot = await app.Read();
                }

                if (runtime.Definition.FindOperation(request.Operation) is { } method)
                {
                    var argument = ParseArgument(method, request.Input);
                    await runtime.Definition.Invoke(runtime.Instance, request.Operation, argument, Json, ct);
                }

                snapshot = await app.Read();
                await PublishViewsAndPresentationAsync(runtime, app, snapshot);
                await app.Progress("idle", $"{runtime.Bundle.Manifest.Title}: {request.Operation}");

                if (appName == "ino" && request.Operation == "open")
                {
                    try
                    {
                        await SiloNeurons.Get<IText>(services, IApp.Key(brain, "desktop") + "/greeted").Set("true");
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogDebug(error, "Could not mark desktop greeted in {Brain}.", brain);
                    }
                }
            }
            finally
            {
                runtime.Gate.Release();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Compiled app {App} did not complete {Operation} in {Brain}.", appName, request.Operation, brain);
        }
    }

    private async Task HandleComposerSubmittedAsync(string brain, ComposerSubmitted signal, CancellationToken ct)
    {
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            if (await HostCaller.ForBrain(services, brain) is { } caller && caller.BrainId == brain)
            {
                CallerContextStamper.Stamp(caller);
            }

            var runtime = GetOrCreateRuntime(brain, "ino");
            var composer = SiloNeurons.Get<IComposer>(services, runtime.AppKey + "/composer");
            var composerState = await composer.Read();
            var submission = composerState.Submissions.FirstOrDefault(item => item.Id == signal.RequestId);
            if (submission is null || string.IsNullOrWhiteSpace(submission.Text))
            {
                return;
            }

            await composer.Settle(signal.RequestId);
            await runtime.Gate.WaitAsync(ct);
            try
            {
                var app = SiloNeurons.Get<IApp>(services, runtime.AppKey);
                var snapshot = await app.Read();
                if (string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
                {
                    await app.Import(runtime.Bundle);
                    snapshot = await app.Read();
                }

                await runtime.Definition.Invoke(runtime.Instance, "send", new InoSend(submission.Text), Json, ct);
                snapshot = await app.Read();
                await PublishViewsAndPresentationAsync(runtime, app, snapshot);
                await app.Progress("idle", "Ino: answered");
            }
            finally
            {
                runtime.Gate.Release();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Ino composer submission {RequestId} failed in {Brain}.", signal.RequestId, brain);
        }
    }

    private async Task HandleViewActionAsync(string brain, string appName, ViewAction signal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signal.Action)) { return; }
        var parts = signal.Action.Split(':', 2, StringSplitOptions.TrimEntries);
        var operation = parts[0];
        var rawArg = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        await HandleAppRequestedAsync(
            brain,
            appName,
            new AppRequested(operation, rawArg, signal.RequestId),
            ct);
    }

    private HostedRuntime GetOrCreateRuntime(string brain, string appName)
        => _runtimes.GetOrAdd((brain, appName), key =>
        {
            var spec = HostedByName[key.Name];
            var definition = new AppDefinition(spec.AppType);
            var instance = definition.Create(_ => null);
            instance.AttachNeuronResolver((contractType, neuronKey) =>
            {
                var fullKey = neuronKey.Contains('/', StringComparison.Ordinal)
                    ? neuronKey
                    : $"{key.Brain}/{neuronKey}";
                return SiloNeurons.Get(services, contractType, fullKey);
            });
            return new HostedRuntime
            {
                Brain = key.Brain,
                Name = key.Name,
                AppKey = IApp.Key(key.Brain, key.Name),
                Bundle = spec.Bundle,
                Definition = definition,
                Instance = instance,
            };
        });

    private async Task PublishViewsAndPresentationAsync(HostedRuntime runtime, IApp app, AppSnapshot snapshot)
    {
        var type = runtime.Instance.GetType();
        var sideView = InvokeRenderMethod(type, runtime.Instance, "RenderSide", "Side");
        var mainView = InvokeRenderMethod(type, runtime.Instance, "Render", runtime.Bundle.Manifest.Title);

        var generation = Math.Max(1L, snapshot.ProgramRevision);
        if (sideView is not null)
        {
            var sideNeuron = SiloNeurons.Get<IView>(services, runtime.AppKey + "/side");
            var currentSide = await sideNeuron.Read();
            var sideGen = currentSide is { Generation: > 0 } ? Math.Max(generation, currentSide.Generation) : generation;
            var nextSideVersion = (currentSide?.Generation == sideGen ? currentSide.Version : 0L) + 1L;
            await sideNeuron.Publish(new UiView(sideGen, nextSideVersion, MapView(sideView, runtime.Brain)));
        }

        if (mainView is not null)
        {
            UiNode mainTree;
            if (runtime.Instance is InoApp ino)
            {
                await SyncInoChatAndComposerAsync(runtime, ino.State);
                var headerExtent = ino.State.ActiveCards.Length > 0 ? 280d : 84d;
                var rightColumn = Ui.Column(
                    "view-main",
                    [
                        MapNode("root", mainView.Root, runtime.Brain),
                        Ui.Reference("transcript", new(UIVocabulary.ChatType, runtime.AppKey + "/transcript")),
                        Ui.Reference("composer", new(UIVocabulary.ComposerType, runtime.AppKey + "/composer")),
                    ],
                    gap: 8,
                    extents: [headerExtent, 0, 88]);
                mainTree = sideView is not null
                    ? Ui.Row("view", [MapNode("side", sideView.Root, runtime.Brain), rightColumn], gap: 12, extents: [224, 0])
                    : rightColumn;
            }
            else if (sideView is not null)
            {
                var sideWidth = runtime.Instance is CreatorApp ? 340d : 236d;
                mainTree = Ui.Row(
                    "view",
                    [
                        MapNode("side", sideView.Root, runtime.Brain),
                        MapNode("root", mainView.Root, runtime.Brain),
                    ],
                    gap: 14,
                    extents: [sideWidth, 0]);
            }
            else
            {
                mainTree = MapView(mainView, runtime.Brain);
            }

            var mainNeuron = SiloNeurons.Get<IView>(services, runtime.AppKey + "/view");
            var currentMain = await mainNeuron.Read();
            var mainGen = currentMain is { Generation: > 0 } ? Math.Max(generation, currentMain.Generation) : generation;
            var nextMainVersion = (currentMain?.Generation == mainGen ? currentMain.Version : 0L) + 1L;
            await mainNeuron.Publish(new UiView(mainGen, nextMainVersion, mainTree));
        }

        var icon = string.IsNullOrWhiteSpace(runtime.Bundle.Manifest.Icon) ? "icon.svg" : runtime.Bundle.Manifest.Icon;
        var presentation = new Presentation(
            new Sphere(icon, []),
            Side: sideView is not null ? new UiChildRef(UIVocabulary.ViewType, runtime.AppKey + "/side") : null,
            Main: new UiChildRef(UIVocabulary.ViewType, runtime.AppKey + "/view"),
            Views: [new PresentationView("full", new UiChildRef(UIVocabulary.ViewType, runtime.AppKey + "/view"))]);
        var previousCaller = CallerContextStamper.TryGet(out var existingCaller) ? existingCaller : null;
        var appCaller = (previousCaller ?? new DigitalBrain.Capabilities.CapabilityCaller(runtime.Brain, runtime.Brain, runtime.Brain)) with
        {
            AppId = runtime.AppKey,
        };
        CallerContextStamper.Stamp(appCaller);
        try
        {
            await app.Present(snapshot.ProgramRevision, presentation);
        }
        finally
        {
            if (previousCaller is not null)
            {
                CallerContextStamper.Stamp(previousCaller);
            }
            else
            {
                RequestContext.Remove(CallerContextStamper.RequestContextKey);
            }
        }
        runtime.Presented = true;
    }

    private async Task SyncInoChatAndComposerAsync(HostedRuntime runtime, InoAppState state)
    {
        var composer = SiloNeurons.Get<IComposer>(services, runtime.AppKey + "/composer");
        var currentComposer = await composer.Read();
        if (currentComposer.Generation < 1 || !string.Equals(currentComposer.Context, runtime.AppKey, StringComparison.Ordinal))
        {
            await composer.Configure(runtime.AppKey, 1L, "Message Ino (local model, SQL, or apps)...");
        }

        var dvKey = IApp.Key(runtime.Brain, "datavisualizer");
        if (state.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Columns is { Length: > 0 } && c.Rows is { Length: > 0 }) is { } tableCard)
        {
            var tableCols = tableCard.Columns!.Select((col, idx) => new TableColumn($"col_{idx + 1}", col)).ToArray();
            var tableRows = tableCard.Rows!.Select(row => (IReadOnlyList<string>)row).ToArray();
            await SiloNeurons.Get<ITable>(services, runtime.AppKey + "/table").Replace(tableCard.Title, tableCols, tableRows);
            await SiloNeurons.Get<ITable>(services, dvKey + "/views/main/table").Replace(tableCard.Title, tableCols, tableRows);
            await SiloNeurons.Get<IText>(services, dvKey + "/last-view").Set("main");
        }

        if (state.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 }) is { } chartCard)
        {
            var chartKind = string.IsNullOrWhiteSpace(chartCard.ChartKind) ? "bar" : chartCard.ChartKind;
            var points = chartCard.ChartPoints!.Select(p => new ChartPoint(p.Id, p.Label, p.Value)).ToArray();
            await SiloNeurons.Get<IChart>(services, runtime.AppKey + "/chart").Render(chartCard.Title, chartKind, points);
            await SiloNeurons.Get<IChart>(services, dvKey + "/views/main/chart").Render(chartCard.Title, chartKind, points);
            await SiloNeurons.Get<IText>(services, dvKey + "/last-view").Set("main");
        }

        var chat = SiloNeurons.Get<IChat>(services, runtime.AppKey + "/transcript");
        var currentChat = await chat.Read();
        var chatGen = currentChat.Projection?.Generation is > 0 and var g ? g : 1L;
        var nextVersion = (currentChat.Projection?.Version ?? 0L) + 1L;
        var entries = new List<ChatEntry>();
        if (state.Turns.Length == 0)
        {
            entries.Add(new ChatEntry(
                "welcome",
                ChatRole.Assistant,
                $"Hello! I am **Ino**, your personal AI assistant in IntoChat OS ({state.Status}).\n\nAsk me anything to chat with your local model (`qwen3.5:4b`), visualize your database as a live **UI Table** and **Chart**, or ask questions about the chart on screen (e.g., *\"What are top 3 on the chart I see?\"*).")
            {
                Model = "qwen3.5:4b",
            });
        }
        else
        {
            for (var i = 0; i < state.Turns.Length; i++)
            {
                var turn = state.Turns[i];
                entries.Add(new ChatEntry($"turn-{i + 1}-user", ChatRole.User, turn.UserMessage));
                entries.Add(BuildAssistantChatEntry(i + 1, turn, state));
            }
        }

        await chat.Project(new ChatProjection(runtime.AppKey, chatGen, nextVersion, [.. entries]));
    }

    private static ChatEntry BuildAssistantChatEntry(int turnNumber, InoTurn turn, InoAppState state)
    {
        var text = turn.Reply;
        var activities = new List<ChatActivity>();
        ChatAppCard? appCard = null;

        if (turn.RouteHint == "postgres:query")
        {
            var tableCard = state.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Columns is { Length: > 0 } && c.Rows is { Length: > 0 });
            if (tableCard is { Columns: { } cols, Rows: { } rows })
            {
                var mdHeader = "| " + string.Join(" | ", cols) + " |";
                var mdSep = "| " + string.Join(" | ", cols.Select(_ => "---")) + " |";
                var mdRows = string.Join("\n", rows.Select(r => "| " + string.Join(" | ", r) + " |"));
                text = $"{turn.Reply}\n\n{mdHeader}\n{mdSep}\n{mdRows}";
            }
            activities.Add(new ChatActivity(
                $"sql-{turnNumber}",
                $"PostgreSQL & Data Visualizer ({state.ConnectedDatabase ?? "supabase"})",
                turn.UserMessage,
                turn.Reply));
            appCard = new ChatAppCard("datavisualizer", "Data Visualizer", "bar-chart", true);
        }
        else if (turn.RouteHint == "chart:qa")
        {
            var chartCard = state.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });
            activities.Add(new ChatActivity(
                $"chart-qa-{turnNumber}",
                $"Chart Q&A ({chartCard?.Title ?? "Active Chart"})",
                turn.UserMessage,
                turn.Reply));
            appCard = new ChatAppCard("datavisualizer", "Data Visualizer", "bar-chart", true);
        }
        else if (turn.RouteHint.StartsWith("postgres:", StringComparison.Ordinal))
        {
            activities.Add(new ChatActivity(
                $"db-{turnNumber}",
                "PostgreSQL Connection",
                turn.UserMessage,
                turn.Reply));
        }

        if (state.ActiveCards.FirstOrDefault(c => c.Id == $"card-{turnNumber}") is { } approvalCard)
        {
            activities.Add(new ChatActivity(
                approvalCard.Id,
                $"{approvalCard.Title} [{approvalCard.Status}]",
                approvalCard.SourceNeuronUri,
                $"Scope: {approvalCard.EffectiveScopeUri} · Status: {approvalCard.Status}"));
        }

        if (turn.RouteHint.StartsWith("routed:", StringComparison.Ordinal))
        {
            var routedTarget = turn.RouteHint["routed:".Length..];
            var dot = routedTarget.IndexOf('.', StringComparison.Ordinal);
            var targetApp = dot > 0 ? routedTarget[..dot] : routedTarget;
            if (ChatAppCard.IsAppName(targetApp))
            {
                appCard = new ChatAppCard(targetApp, targetApp, "grid", true);
            }
        }

        return new ChatEntry($"turn-{turnNumber}-assistant", ChatRole.Assistant, text)
        {
            Model = turn.RouteHint,
            Activities = [.. activities],
            App = appCard,
        };
    }

    private static View? InvokeRenderMethod(Type type, App instance, string methodName, string fallbackTitle)
    {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        if (method is null || !(typeof(View).IsAssignableFrom(method.ReturnType) || typeof(Node).IsAssignableFrom(method.ReturnType)))
        {
            return null;
        }
        var value = method.Invoke(instance, null);
        return value as View ?? (value is Node node ? new View(fallbackTitle, node) : null);
    }

    private static UiNode MapView(View view, string? brainId = null)
        => AppNeuron.MapView(view, brainId);

    private static UiNode MapNode(string key, Node node, string? brainId = null)
        => AppNeuron.MapNode(key, node, brainId);

    private static object? ParseArgument(MethodInfo method, string input)
    {
        var parameter = method.GetParameters().FirstOrDefault(p => p.ParameterType != typeof(CancellationToken));
        if (parameter is null) { return null; }
        var targetType = parameter.ParameterType;
        if (targetType == typeof(string))
        {
            return string.IsNullOrEmpty(input) ? null : input;
        }
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return JsonSerializer.Deserialize(trimmed, targetType, Json);
        }

        var unquoted = trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
        if (targetType == typeof(InoOpen)) { return new InoOpen(unquoted); }
        if (targetType == typeof(InoSend)) { return new InoSend(unquoted); }
        if (targetType == typeof(InoConnectDb)) { return new InoConnectDb(unquoted); }
        if (targetType == typeof(InoQueryDb)) { return new InoQueryDb(unquoted); }
        if (targetType == typeof(InoApproveCard)) { return new InoApproveCard(unquoted, true); }
        if (targetType == typeof(InoMake)) { return new InoMake(unquoted); }
        if (targetType == typeof(GalleryOpen)) { return new GalleryOpen(unquoted); }
        if (targetType == typeof(GalleryFilter)) { return new GalleryFilter(unquoted); }
        if (targetType == typeof(GallerySelect)) { return new GallerySelect(unquoted); }
        if (targetType == typeof(GallerySwitchIdentity)) { return new GallerySwitchIdentity(unquoted); }
        if (targetType == typeof(CreatorInspect)) { return new CreatorInspect(unquoted); }
        if (targetType == typeof(CreatorPrompt)) { return new CreatorPrompt(unquoted); }
        if (targetType == typeof(PostPublish)) { return new PostPublish(unquoted); }
        return null;
    }

    private sealed class HostedRuntime
    {
        public required string Brain { get; init; }
        public required string Name { get; init; }
        public required string AppKey { get; init; }
        public required AppBundle Bundle { get; init; }
        public required AppDefinition Definition { get; init; }
        public required App Instance { get; init; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Presented { get; set; }
    }
}

internal sealed class CompiledAppsLifecycle(IServiceProvider services) : ILifecycleParticipant<ISiloLifecycle>
{
    public void Participate(ISiloLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        lifecycle.Subscribe<CompiledAppsLifecycle>(
            ServiceLifecycleStage.Active,
            cancellationToken => ActivatorUtilities.CreateInstance<CompiledAppsHost>(services).Execute(cancellationToken));
    }
}
