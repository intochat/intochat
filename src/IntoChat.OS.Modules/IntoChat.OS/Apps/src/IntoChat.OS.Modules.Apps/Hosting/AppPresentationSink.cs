using DigitalBrain.Brains;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Apps;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.UI;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chart;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Table;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Views;
using Orleans.Runtime;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Modules.Apps;

internal sealed class AppPresentationSink(IServiceProvider services) : IPresentationSink
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (View? Side, View? Main, long Sequence)> _presented = new(StringComparer.Ordinal);

    public View? TryGetPresentedView(string appKey)
        => _presented.TryGetValue(appKey, out var entry) ? entry.Main : null;

    public (View? Side, View? Main, long Sequence) TryGetPresentedViews(string appKey)
        => _presented.TryGetValue(appKey, out var entry) ? entry : (null, null, 0L);

    public async Task Present(string owner, View? side, View? main, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var brain = BrainKey.BrainOf(owner);
        var appKey = owner.Contains("/apps/", StringComparison.Ordinal) ? owner : IApp.Key(brain, owner);
        var appName = IApp.NameOf(brain, appKey) ?? owner;
        _presented.AddOrUpdate(
            appKey,
            static (_, v) => (v.Side, v.Main, 1L),
            static (_, prev, v) => (v.Side, v.Main, prev.Sequence + 1L),
            (Side: side, Main: main));

        var app = SiloNeurons.Get<IApp>(services, appKey);
        var snapshot = await app.Read().WaitAsync(ct);
        var generation = Math.Max(1L, snapshot.ProgramRevision);

        if (side is not null)
        {
            var sideNeuron = SiloNeurons.Get<IView>(services, appKey + "/side");
            var currentSide = await sideNeuron.Read();
            var sideGen = currentSide is { Generation: > 0 } ? Math.Max(generation, currentSide.Generation) : generation;
            var nextSideVersion = (currentSide?.Generation == sideGen ? currentSide.Version : 0L) + 1L;
            await sideNeuron.Publish(new UiView(sideGen, nextSideVersion, AppNeuron.MapView(side, brain)));
        }

        if (main is not null)
        {
            var host = services.GetService(typeof(InProcessAppHost)) as InProcessAppHost;
            var instance = host?.TryGetTurns(owner)?.App;

            UiNode mainTree;
            if (instance is DigitalBrain.App<InoAppState> ino)
            {
                await SyncInoChatAndComposerAsync(brain, appKey, ino.State);
                mainTree = side is not null
                    ? Ui.Row(
                        "ino-workspace",
                        [
                            AppNeuron.MapNode("ino-side", side.Root, brain),
                            AppNeuron.MapNode("ino-main", main.Root, brain),
                        ],
                        gap: 0,
                        extents: [256, 0])
                    : AppNeuron.MapNode("ino-main", main.Root, brain);
            }
            else if (side is not null)
            {
                var sideWidth = appName == "creator" ? 340d : appName == "gallery" ? 268d : 236d;
                mainTree = Ui.Row(
                    "view",
                    [
                        AppNeuron.MapNode("side", side.Root, brain),
                        AppNeuron.MapNode("root", main.Root, brain),
                    ],
                    gap: 14,
                    extents: [sideWidth, 0]);
            }
            else
            {
                mainTree = AppNeuron.MapView(main, brain);
            }

            var mainNeuron = SiloNeurons.Get<IView>(services, appKey + "/view");
            var currentMain = await mainNeuron.Read();
            if (appName != "desktop" || currentMain?.Root is not { Key: "desktop" })
            {
                var mainGen = currentMain is { Generation: > 0 } ? Math.Max(generation, currentMain.Generation) : generation;
                var nextMainVersion = (currentMain?.Generation == mainGen ? currentMain.Version : 0L) + 1L;
                await mainNeuron.Publish(new UiView(mainGen, nextMainVersion, mainTree));
            }
        }

        var icon = string.IsNullOrWhiteSpace(snapshot.Manifest.Icon) ? "icon.svg" : snapshot.Manifest.Icon;
        var presentation = new Presentation(
            new Sphere(icon, []),
            Side: side is not null ? new UiChildRef(UIVocabulary.ViewType, appKey + "/side") : null,
            Main: new UiChildRef(UIVocabulary.ViewType, appKey + "/view"),
            Views: [new PresentationView("full", new UiChildRef(UIVocabulary.ViewType, appKey + "/view"))]);

        var previousCaller = RuntimeCallerContext.TryGet(out var existingCaller) ? existingCaller : null;
        var baseCaller = previousCaller ?? await HostCaller.ForBrain(services, brain);
        if (baseCaller is null)
        {
            return;
        }
        var appCaller = baseCaller with
        {
            AppId = appKey,
        };
        RuntimeCallerContext.Stamp(appCaller);
        try
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Manifest.Title))
            {
                await app.Present(snapshot.ProgramRevision, presentation);
            }
        }
        finally
        {
            if (previousCaller is not null)
            {
                RuntimeCallerContext.Stamp(previousCaller);
            }
            else
            {
                RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
            }
        }
    }

    private async Task SyncInoChatAndComposerAsync(string brain, string appKey, InoAppState state)
    {
        var composer = SiloNeurons.Get<IComposer>(services, appKey + "/composer");
        var currentComposer = await composer.Read();
        if (currentComposer.Generation < 1 || !string.Equals(currentComposer.Context, appKey, StringComparison.Ordinal))
        {
            await composer.Configure(appKey, 1L, "Message Ino (local model, SQL, PhotoCraft, or apps)...");
        }

        var dvKey = IApp.Key(brain, "datavisualizer");
        if (state.ActiveCards.LastOrDefault(c => c.Kind == "table" && c.Columns is { Length: > 0 } && c.Rows is { Length: > 0 }) is { } tableCard)
        {
            var tableCols = tableCard.Columns!.Select((col, idx) => new TableColumn($"col_{idx + 1}", col)).ToArray();
            var tableRows = tableCard.Rows!.Select(row => (IReadOnlyList<string>)row).ToArray();
            await SiloNeurons.Get<ITable>(services, appKey + "/table").Replace(tableCard.Title, tableCols, tableRows);
            await SiloNeurons.Get<ITable>(services, dvKey + "/views/main/table").Replace(tableCard.Title, tableCols, tableRows);
            await SiloNeurons.Get<IText>(services, dvKey + "/last-view").Set("main");
        }

        if (state.ActiveCards.LastOrDefault(c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 }) is { } chartCard)
        {
            var chartKind = string.IsNullOrWhiteSpace(chartCard.ChartKind) ? "bar" : chartCard.ChartKind;
            var points = chartCard.ChartPoints!.Select(p => new ChartPoint(p.Id, p.Label, p.Value)).ToArray();
            await SiloNeurons.Get<IChart>(services, appKey + "/chart").Render(chartCard.Title, chartKind, points);
            await SiloNeurons.Get<IChart>(services, dvKey + "/views/main/chart").Render(chartCard.Title, chartKind, points);
            await SiloNeurons.Get<IText>(services, dvKey + "/last-view").Set("main");
        }

        var chat = SiloNeurons.Get<IChat>(services, appKey + "/transcript");
        var currentChat = await chat.Read();
        var chatGen = currentChat.Projection?.Generation is > 0 and var g ? g : 1L;
        var nextVersion = (currentChat.Projection?.Version ?? 0L) + 1L;
        var entries = new List<ChatEntry>();
        if (state.Turns.Length == 0)
        {
            entries.Add(new ChatEntry(
                "welcome",
                ChatRole.Assistant,
                $"Hello! I am **Ino**, your personal AI assistant in IntoChat OS ({state.Status}).\n\nAsk me anything to chat with your local model (`qwen3.5:4b`), edit photos or videos in **PhotoCraft** and **FilmCraft** (e.g., *\"remove background from portrait.png\"*), or visualize your database as a live **UI Table** and **Chart**.")
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

        await chat.Project(new ChatProjection(appKey, chatGen, nextVersion, [.. entries]));
    }

    internal static ChatEntry BuildAssistantChatEntry(int turnNumber, InoTurn turn, InoAppState state)
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

        if (state.ActiveCards.LastOrDefault(c => c.Id == $"studio-{turnNumber}") is { } studioCard)
        {
            activities.Add(new ChatActivity(
                studioCard.Id,
                studioCard.Title,
                studioCard.SourceNeuronUri,
                studioCard.Summary));
        }

        if (turn.RouteHint.StartsWith("routed:", StringComparison.Ordinal))
        {
            var routedTarget = turn.RouteHint["routed:".Length..];
            var dot = routedTarget.IndexOf('.', StringComparison.Ordinal);
            var targetApp = dot > 0 ? routedTarget[..dot] : routedTarget;
            if (ChatAppCard.IsAppName(targetApp))
            {
                var displayTitle = targetApp switch
                {
                    "photocraft" => "PhotoCraft",
                    "filmcraft" => "FilmCraft",
                    "settings" => "Settings",
                    "files" => "Files",
                    _ => targetApp,
                };
                var displayIcon = targetApp switch
                {
                    "photocraft" => "image",
                    "filmcraft" => "film",
                    "settings" => "settings",
                    "files" => "folder",
                    _ => "grid",
                };
                appCard = new ChatAppCard(targetApp, displayTitle, displayIcon, true);
            }
        }

        return new ChatEntry($"turn-{turnNumber}-assistant", ChatRole.Assistant, text)
        {
            Model = turn.RouteHint,
            Activities = [.. activities],
            App = appCard,
        };
    }
}
