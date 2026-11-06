using DigitalBrain.Capabilities.Connections;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Runtime.Orleans.Testing;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Views;
using DigitalBrain.UI;
using IntoChat.Files;
using IntoChat.OS.Authority;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Runtime.Orleans.Confirmations;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Data;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.Aspire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ui = IntoChat.OS.Modules.Flutter.Views.Ui;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// Restores the historical E2E app workflows (DesktopStartFacts, AppsAppFacts, ShippedAppJourneys,
/// InoChatFacts, MemoryLoopFacts, InoSecretFacts, SelfImproveFacts, and Files/Storage broker journeys)
/// against the real DigitalBrain SiloHost environment.
/// </summary>
public sealed class RestoredAppsE2EFacts(ProductHostFixture fixture)
    : IClassFixture<ProductHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DesktopSettingsGalleryPhotoCraftAndFilmCraftLifecycleRunsOnSilo()
    {
        var brain = fixture.NewBrain("shell");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
        var settingsNeuron = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");
        var profileNeuron = SiloNeurons.Get<IProfile>(fixture.Services, $"{brain}/profile");

        await settingsNeuron.SetProfile("Ada Lovelace", "light", "gpt-5");
        await settingsNeuron.SetUiIdentity("spatial-glass", "compact-pill");
        var profileState = await profileNeuron.SetBio("ada", "Analytical Engine Architect", ["ino", "creator"]);
        Assert.Equal("ada", profileState.Handle);
        Assert.Equal("Analytical Engine Architect", profileState.Bio);

        // 1. DesktopApp lifecycle on the real silo (DesktopStartFacts + ShippedAppJourneys).
        var desktop = fixture.AttachSilo(new DesktopApp(), brain);
        await desktop.Start(new DesktopStart());
        Assert.Equal("smart-desktop", desktop.State.LayoutMode);
        Assert.Equal("spatial-glass", desktop.State.StylePreset);
        Assert.Equal("compact-pill", desktop.State.BarLayout);
        Assert.True(desktop.State.ActiveNeurons >= 9);
        Assert.Contains("ino", desktop.State.PinnedApps);
        Assert.Contains("creator", desktop.State.PinnedApps);

        await desktop.Layout(new DesktopLayoutChoice());
        Assert.Equal("spatial-grid", desktop.State.LayoutMode);

        await desktop.Narrow(new DesktopNarrow("true"));
        Assert.True(desktop.State.IsNarrow);

        await desktop.Pin(new DesktopPinApp("photocraft", Pinned: true));
        Assert.Contains("photocraft", desktop.State.PinnedApps);

        await desktop.AutoArrange(new DesktopAutoArrange());
        Assert.Equal("whiteboard-auto-arranged", desktop.State.LayoutMode);
        Assert.Contains("photocraft", desktop.State.PinnedApps);

        await fixture.PublishAppViewToSiloAsync(brain, "desktop", desktop.RenderSide(), desktop.Render());
        var desktopView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/desktop/view").Read();
        Assert.NotNull(desktopView?.Root);

        // 2. SettingsApp lifecycle on the real silo (AppsAppFacts + Settings preferences & connections).
        var settingsApp = fixture.AttachSilo(new SettingsApp(), brain);
        await settingsApp.Open(new SettingsOpen());
        Assert.Equal("Ada Lovelace", settingsApp.State.DisplayName);
        Assert.Equal("light", settingsApp.State.Theme);
        Assert.Equal("gpt-5", settingsApp.State.DefaultModel);

        await settingsApp.SavePreferences(new SettingsSavePreferences(
            DisplayName: "Grace Hopper",
            Theme: "dark",
            DefaultModel: "qwen3.5:4b",
            StylePreset: "cyber-synaptic",
            BarLayout: "split-top-dock"));
        Assert.Equal("Grace Hopper", settingsApp.State.DisplayName);
        Assert.Equal("dark", settingsApp.State.Theme);
        Assert.Equal("qwen3.5:4b", settingsApp.State.DefaultModel);
        Assert.Equal("cyber-synaptic", settingsApp.State.StylePreset);
        Assert.Equal("split-top-dock", settingsApp.State.BarLayout);

        var persistedSettings = await settingsNeuron.Read();
        Assert.Equal("Grace Hopper", persistedSettings.DisplayName);
        Assert.Equal("dark", persistedSettings.Theme);
        Assert.Equal("qwen3.5:4b", persistedSettings.DefaultModel);
        Assert.Equal("cyber-synaptic", persistedSettings.StylePreset);
        Assert.Equal("split-top-dock", persistedSettings.BarLayout);

        settingsApp.RefreshConnections(new SettingsRefreshConnections(
            ConnectionKey: "neuron://local/connection/postgres/analytics",
            FriendApp: "app:local/apps/datavisualizer"));
        Assert.Contains("neuron://local/connection/postgres/analytics", settingsApp.State.ConnectionKeys!);

        await fixture.PublishAppViewToSiloAsync(brain, "settings", settingsApp.RenderSide(), settingsApp.Render());
        var settingsView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/settings/view").Read();
        Assert.NotNull(settingsView?.Root);

        // 3. GalleryApp lifecycle on the real silo.
        var gallery = fixture.AttachSilo(new GalleryApp(), brain);
        await gallery.Open(new GalleryOpen());
        Assert.Equal("cyber-synaptic", gallery.State.StylePreset);
        Assert.Equal("split-top-dock", gallery.State.BarLayout);

        gallery.Filter(new GalleryFilter("chat-lfp", "Table"));
        Assert.Equal("table-card", gallery.State.SelectedId);

        gallery.Select(new GallerySelect("brain-scene-3d"));
        Assert.Equal("brain-scene-3d", gallery.State.SelectedId);

        await gallery.SwitchIdentity(new GallerySwitchIdentity("fluent-acrylic", "floating-glass"));
        Assert.Equal("fluent-acrylic", (await settingsNeuron.Read()).StylePreset);

        await fixture.PublishAppViewToSiloAsync(brain, "gallery", gallery.RenderSide(), gallery.Render());
        var galleryView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/gallery/view").Read();
        Assert.NotNull(galleryView?.Root);

        // 4. PhotoCraftApp dual-mode (Native Linux CLI + MCP) lifecycle on the real silo.
        var photo = fixture.AttachSilo(new PhotoCraftApp(), brain);
        await photo.Open(new PhotoCraftStudioOpen("/work/hero-product.png", "native-linux"));
        await photo.RemoveBackground(new PhotoCraftRemoveBackground("/work/hero-product.png", "native-linux"));
        Assert.Equal("layer.removeBackground", photo.State.LastCommandId);
        Assert.Equal("native-linux", photo.State.ExecutionMode);
        Assert.EndsWith(".nobg.png", photo.State.OutputDocument, StringComparison.Ordinal);

        photo.SwitchMode(new PhotoCraftSwitchMode("mcp"));
        await photo.Edit(new PhotoCraftStudioEdit("make a 512x512 image with a blue-to-orange gradient", "/work/canvas.psd", "mcp"));
        Assert.Equal("mcp", photo.State.ExecutionMode);
        Assert.Equal("layer.newFillLayer.gradient", photo.State.LastCommandId);
        Assert.Equal(2, photo.State.History.Length);

        await fixture.PublishAppViewToSiloAsync(brain, "photocraft", photo.RenderSide(), photo.Render());
        var photoView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/photocraft/view").Read();
        Assert.NotNull(photoView?.Root);

        // 5. FilmCraftApp dual-mode (Native Linux CLI + MCP) lifecycle on the real silo.
        var film = fixture.AttachSilo(new FilmCraftApp(), brain);
        await film.Open(new FilmCraftStudioOpen("/work/launch-trailer.fcproj", "native-linux"));
        await film.Edit(new FilmCraftStudioEdit("razor split timeline at 00:00:03:12", "/work/launch-trailer.fcproj", "native-linux"));
        Assert.Equal("timeline.razor", film.State.LastCommandId);
        Assert.Equal("native-linux", film.State.ExecutionMode);

        film.SwitchMode(new FilmCraftSwitchMode("mcp"));
        await film.Export(new FilmCraftExport("h264-1080p60", "/work/launch-trailer.fcproj", "mcp"));
        Assert.Equal("mcp", film.State.ExecutionMode);
        Assert.Equal("sequence.exportMp4", film.State.LastCommandId);
        Assert.EndsWith(".1080p60.mp4", film.State.OutputFile, StringComparison.Ordinal);

        await fixture.PublishAppViewToSiloAsync(brain, "filmcraft", film.RenderSide(), film.Render());
        var filmView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/filmcraft/view").Read();
        Assert.NotNull(filmView?.Root);

        // 6. MarkdrawApp and DataStudioApp lifecycle on the real silo.
        var markdraw = fixture.AttachSilo(new MarkdrawApp(), brain);
        await markdraw.Open(new MarkdrawOpen("/work/system-architecture.markdraw", "flow"));
        await markdraw.PatchElements(new MarkdrawPatchElements("add rectangle \"API Gateway\" at 140,120 180x84 color #38bdf8", "ino"));
        Assert.True(markdraw.State.Elements.Length >= 5);
        await fixture.PublishAppViewToSiloAsync(brain, "markdraw", markdraw.RenderSide(), markdraw.Render());
        var markdrawView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/markdraw/view").Read();
        Assert.NotNull(markdrawView?.Root);

        var dataStudio = fixture.AttachSilo(new DataStudioApp(), brain);
        await dataStudio.Open(new DataStudioOpen("/work/q4-opportunities.csv"));
        await dataStudio.Query(new DataStudioQuery("SELECT stage, SUM(amount) FROM /work/q4-opportunities.csv GROUP BY stage"));
        Assert.NotEmpty(dataStudio.State.Rows);
        await fixture.PublishAppViewToSiloAsync(brain, "data", dataStudio.RenderSide(), dataStudio.Render());
        var dataView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/data/view").Read();
        Assert.NotNull(dataView?.Root);
    }

    [Fact]
    public async Task CreatorStudioWorkflowInspectsAspireTelemetryAndVerifiesBddScenariosOnSilo()
    {
        var brain = fixture.NewBrain("creator");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));

        // 1. Seed live error logs and distributed traces in AspireNeuron on the real silo (SelfImproveFacts).
        var aspire = SiloNeurons.Get<IAspire>(fixture.Services, $"{brain}/aspire");
        await aspire.RecordLogAsync(new AspireLogEntry(
            DateTimeOffset.UtcNow,
            "OperatingSystem",
            "Error",
            "DivideByZeroException in calculator",
            "System.DivideByZeroException"));
        await aspire.RecordTraceAsync(new AspireTraceSpan(
            "trace-calc-01",
            "span-calc-01",
            "OperatingSystem",
            "calculator.divide",
            "Error",
            850,
            "Attempted to divide by zero."));

        // 2. Open CreatorApp on the real silo and inspect telemetry + 3D BrainScene nodes.
        var creator = fixture.AttachSilo(new CreatorApp(), brain);
        await creator.Open(new CreatorOpen());
        Assert.Contains("3D Brain ready", creator.State.Status, StringComparison.Ordinal);
        Assert.Contains(creator.State.DiagnosedErrors, err => err.Contains("DivideByZeroException in calculator", StringComparison.Ordinal));

        await creator.Inspect(new CreatorInspect("OperatingSystem"));
        Assert.Contains(creator.State.DiagnosedErrors, err => err.Contains("Attempted to divide by zero.", StringComparison.Ordinal));

        await creator.Inspect(new CreatorInspect("bdd"));
        Assert.Equal("bdd", creator.State.SelectedNodeId);
        Assert.True(creator.State.SceneRotationPaused);
        Assert.True(creator.State.CameraZoom >= 1.5);

        // 3. Prompt CreatorApp to synthesize & compile C# App<TState> with Roslyn in-memory, run BDD gate, and record in BddNeuron on the silo.
        await creator.Prompt(new CreatorPrompt("Fix DivideByZeroException in calculator", "calculator"));
        Assert.Equal("calculator", creator.State.TargetApp);
        Assert.True(creator.State.SandboxCompiled);
        Assert.True(creator.State.Promoted);
        Assert.NotNull(creator.State.LastBddReport);
        Assert.True(creator.State.LastBddReport.Passed);
        Assert.Contains("[App(\"calculator\")]", creator.State.GeneratedSource, StringComparison.Ordinal);
        Assert.Contains("DivideByZeroException", creator.State.GeneratedSource, StringComparison.Ordinal);

        // Verify the BddReport was persisted into BddNeuron on the real Orleans silo.
        var siloBdd = SiloNeurons.Get<IBdd>(fixture.Services, new NeuronId(new BrainId(brain), "bdd", "bdd"));
        var persistedReport = await siloBdd.Read(Ct);
        Assert.NotNull(persistedReport);
        Assert.True(persistedReport.Passed);
        Assert.Equal("calculator", persistedReport.AppId);

        // 4. Switch plain-language BDD scenario debugger locale, bump version, rollback, and hot-restart sandbox.
        await creator.Language(new CreatorSwitchLanguage("uk"));
        Assert.Equal("uk", creator.State.BddLanguage);
        Assert.NotEmpty(creator.State.ScenarioSteps ?? []);

        var versionAfterFirstPrompt = creator.State.CurrentVersion;
        await creator.Prompt(new CreatorPrompt("Add memory recall button to calculator", "calculator"));
        var versionAfterSecondPrompt = creator.State.CurrentVersion;
        Assert.NotEqual(versionAfterFirstPrompt, versionAfterSecondPrompt);

        await creator.Rollback(new CreatorRollback(versionAfterFirstPrompt));
        Assert.Equal(versionAfterFirstPrompt, creator.State.CurrentVersion);

        await creator.HotRestart(new CreatorHotRestart());
        Assert.True(creator.State.SandboxRestartCount >= 1);

        await fixture.PublishAppViewToSiloAsync(brain, "creator", creator.RenderSide(), creator.Render());
        var creatorView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/creator/view").Read();
        Assert.NotNull(creatorView?.Root);
    }

    [Fact]
    public async Task InoChatMemoryLoopDatabaseQueryStudioRoutingAndSecretPromptRunOnSilo()
    {
        var brain = fixture.NewBrain("ino");
        using var stamp = RuntimeCallerContext.Stamp(CapabilityCallers.Of(brain, brain, brain));
        var memory = SiloNeurons.Get<IMemory>(fixture.Services, $"{brain}/memory");
        var settings = SiloNeurons.Get<IUserSettings>(fixture.Services, $"{brain}/settings");

        await settings.SetProfile("Astrid Lindqvist", "dark", "qwen3.5:4b");
        await memory.RememberFact("preference", "Astrid oversees Nordlys Q4 enterprise renewals.", importance: 9);

        var ino = fixture.AttachSilo(new InoApp(), brain);
        await ino.Open(new InoOpen("greet"));
        Assert.Contains("Astrid Lindqvist", ino.State.Status, StringComparison.Ordinal);

        // 1. Memory loop (MemoryLoopFacts + InoChatFacts): recall seeded fact and capture new episode in MemoryNeuron on the silo.
        await ino.Send(new InoSend("Summarize Nordlys Q4 enterprise renewals"));
        var lastTurn = Assert.Single(ino.State.Turns);
        Assert.True(lastTurn.RecalledCount >= 1);
        Assert.Contains("Nordlys Q4 enterprise renewals", lastTurn.Reply, StringComparison.Ordinal);

        var recalledAfterTurn = await memory.Recall("Summarize Nordlys", 5);
        Assert.NotEmpty(recalledAfterTurn);

        // Live settings synchronization into InoApp.
        var updatedSettings = await settings.SetProfile("ElonWatcher", "dark", "qwen3.5:4b");
        ino.OnSettingsChanged(new UserSettingsChanged(
            updatedSettings.DisplayName,
            updatedSettings.Theme,
            updatedSettings.DefaultModel,
            updatedSettings.StylePreset,
            updatedSettings.BarLayout));
        Assert.Contains("Synced settings: ElonWatcher (qwen3.5:4b)", ino.State.Status, StringComparison.Ordinal);

        // 2. Approval card creation and 1-click confirmation on the silo.
        await ino.Send(new InoSend("Draft confirmation card for Q4 close date"));
        var approvalCard = Assert.Single(ino.State.ActiveCards, c => c.Kind == "approval");
        Assert.Equal("pending", approvalCard.Status);
        Assert.Equal("neuron://local/os.confirmations/inbox", approvalCard.SourceNeuronUri);

        await ino.ApproveCard(new InoApproveCard(approvalCard.Id, Approved: true));
        Assert.Equal("approved", Assert.Single(ino.State.ActiveCards, c => c.Id == approvalCard.Id).Status);

        // 3. Database connection & SQL query visualization producing Table + Chart cards on the silo.
        await ino.ConnectDb(new InoConnectDb("Host=db.nordlys.internal;Database=nordlys_crm;Username=readonly", "main"));
        Assert.Contains("nordlys_crm", ino.State.ConnectedDatabase, StringComparison.Ordinal);
        await ino.QueryDb(new InoQueryDb("SELECT account, stage, amount FROM opportunities"));
        Assert.Contains(ino.State.ActiveCards, c => c.Kind == "table" && c.Rows is { Length: > 0 });
        Assert.Contains(ino.State.ActiveCards, c => c.Kind == "chart" && c.ChartPoints is { Length: > 0 });

        // 4. Studio routing to PhotoCraft and FilmCraft.
        await ino.Send(new InoSend("Remove background from portrait.png"));
        Assert.Contains(ino.State.Turns, t => t.RouteHint.Contains("photocraft", StringComparison.OrdinalIgnoreCase)
            || t.Reply.Contains("layer.removeBackground", StringComparison.Ordinal));

        await ino.Send(new InoSend("Razor split timeline in FilmCraft via mcp"));
        Assert.Contains(ino.State.Turns, t => t.RouteHint.Contains("filmcraft", StringComparison.OrdinalIgnoreCase)
            || t.Reply.Contains("timeline.razor", StringComparison.Ordinal));

        // 5. Secret prompt workflow (InoSecretFacts): /scenario invoice-check emits SecretCardNode routed to os://window/secure and never leaks secrets.
        var forbiddenText = string.Concat("sample", "-", "unexposed", "-", "value");
        await ino.Send(new InoSend("/scenario invoice-check"));
        var secretCardState = Assert.Single(ino.State.ActiveCards, c => c.Kind == "secret");
        Assert.Equal("Tripletex Session Token", secretCardState.Title);
        Assert.Equal("neuron://local/vault/secrets#TRIPLETEX_SESSION_TOKEN", secretCardState.SourceNeuronUri);

        var renderedView = ino.Render();
        var secretNode = FindSecretCard(renderedView.Root);
        Assert.NotNull(secretNode);
        Assert.Equal("Tripletex Session Token", secretNode.Name);
        Assert.Contains("neuron://local/vault/secrets", secretNode.SavedIn, StringComparison.Ordinal);
        Assert.Equal("os://window/secure", secretNode.OnEnter?.Neuron);

        Assert.DoesNotContain(ino.State.Turns, t => t.Reply.Contains(forbiddenText, StringComparison.Ordinal)
            || t.UserMessage.Contains(forbiddenText, StringComparison.Ordinal));

        await fixture.PublishAppViewToSiloAsync(brain, "ino", ino.RenderSide(), renderedView);
        var inoSiloView = await SiloNeurons.Get<IView>(fixture.Services, $"{brain}/apps/ino/view").Read();
        Assert.NotNull(inoSiloView?.Root);
    }

    [Fact]
    public async Task FilesAppResolvesStorageThroughCapabilityBrokerOnSilo()
    {
        var brain = fixture.NewBrain("files");
        var brainId = new BrainId(brain);
        var appKey = $"{brain}/apps/files";

        using (RuntimeCallerContext.Stamp(CapabilityCallers.Of("user-owner", "user-owner", brain, CallerKind.App, appKey)))
        {
            var broker = fixture.Services.GetRequiredService<ICapabilityBroker>();
            var files = new FilesApp(
                broker is CapabilityBroker runtimeBroker ? runtimeBroker.ForApp(appKey) : broker,
                brainId,
                new FilesState());

            await files.Open(new FilesOpened(), Ct);
            Assert.NotEmpty(files.Snapshot.Storages);
            var storageEntry = Assert.Single(files.Snapshot.Storages, s => s.Connection == "local-disk");
            Assert.Null(storageEntry.Error);
            Assert.NotEmpty(files.Snapshot.Items);

            // Browse into the root folder and write a test file through the brokered storage.
            var rootItem = files.Snapshot.Items[0];
            await files.Browse(new FilesLocation(rootItem.Connection, rootItem.Item.Id), Ct);
            Assert.Equal("local-disk", files.Snapshot.Connection);
            Assert.Equal(rootItem.Item.Id, files.Snapshot.Folder);

            var writePlan = files.Plan(
                "write",
                "architecture-notes.md",
                "# IntoChat OS\nAll E2E workflows run on the real DigitalBrain silo.");
            var resolved = Assert.Single(await broker.ResolveAll<IStorage>(brainId, Ct));
            var writeOpId = await resolved.Capability.Write(writePlan, Ct);
            Assert.False(string.IsNullOrWhiteSpace(writeOpId.Value));

            // Re-browse, search, select, preview, and hand off to Ino with a canonical neuron:// URI.
            await files.Browse(new FilesLocation(rootItem.Connection, rootItem.Item.Id), Ct);
            Assert.Contains(files.Snapshot.Items, item => item.Item.Name == "architecture-notes.md");

            await files.Search(new FilesSearch("architecture-notes", Everywhere: true), Ct);
            var foundItem = Assert.Single(files.Snapshot.Items, item => item.Item.Name == "architecture-notes.md");

            await files.OpenResult(foundItem, Ct);
            Assert.NotNull(files.Snapshot.Selected);
            Assert.Equal("architecture-notes.md", files.Snapshot.Selected.Item.Name);
            Assert.Contains("# IntoChat OS", files.Snapshot.Preview, StringComparison.Ordinal);
            Assert.Equal($"neuron://{brain}/local-disk.files/architecture-notes.md", files.Snapshot.SelectedNeuronUri);

            var handoffPrompt = files.AskIno();
            Assert.Contains($"neuron://{brain}/local-disk.files/architecture-notes.md", handoffPrompt, StringComparison.Ordinal);

            // Verify safe write planning (mkdir, rename, delete) and brokered connection binding on the silo.
            var mkdirPlan = files.Plan("mkdir", name: "docs");
            Assert.Equal(WriteKind.CreateFolder, mkdirPlan.Kind);
            var renamePlan = files.Plan("rename", name: "architecture-notes-v2.md");
            Assert.Equal(WriteKind.Rename, renamePlan.Kind);
            var deletePlan = files.Plan("delete");
            Assert.Equal(WriteKind.Delete, deletePlan.Kind);

            Assert.Contains(
                fixture.BoundStorageCalls,
                entry => entry.Brain == brainId && entry.ConnectionKey == "local-disk" && entry.CallerApp == appKey);
        }
    }

    private static SecretCardNode? FindSecretCard(Node? node)
    {
        if (node is null)
        {
            return null;
        }
        if (node is SecretCardNode secret)
        {
            return secret;
        }
        foreach (var child in ChildrenOf(node))
        {
            if (FindSecretCard(child) is { } match)
            {
                return match;
            }
        }
        return null;
    }

    private static IEnumerable<Node> ChildrenOf(Node node) => node switch
    {
        Column col => col.Children,
        Row row => row.Children,
        Surface surface => surface.Children,
        Grid grid => grid.Children,
        SplitPane split => [split.Leading, split.Trailing],
        AppShellNode shell => new[] { shell.Sidebar, shell.Toolbar, shell.Body, shell.Inspector }.OfType<Node>(),
        _ => [],
    };
}
