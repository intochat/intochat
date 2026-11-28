using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Mcp;
using IntoChat.Treg;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Components.Treg;

public sealed class SkillImportFacts
{
    private static readonly BrainId Brain = new("local");

    private const string ReaMarketingSkillMarkdown = """
        ---
        name: "rea-marketing"
        description: "Use when the user wants to reverse engineer a brand's holiday marketing across ads, emails, and landing pages."
        ---

        # Reverse Engineer Holiday Marketing

        Follow a 3-step research workflow using treg tools (`catalog_search`, `catalog_get`, and `call`).

        1. Discover relevant ad library and web scraping routes via `catalog_search`.
        2. Inspect exact schemas and per-call USD prices via `catalog_get`.
        3. Execute approved routes via `call` and synthesize the teardown.

        ## Starters
        - "Reverse engineer Nike's Black Friday campaign"
        - "Compare Glossier vs Rhode holiday landing pages"
        """;

    [Fact]
    public void TregPresetDefaultUsesVerifiedStandardEndpointBearerAuthToolsAndOncePerSession()
    {
        var first = TregPreset.Default();
        var second = TregPreset.Default();

        Assert.Equal("https://treg.to/mcp/", first.Url);
        Assert.Equal("treg", first.Title);
        Assert.Equal("Authorization", first.HeaderName);
        Assert.Equal("Bearer ", first.HeaderPrefix);
        Assert.Equal(McpConfirmationMode.OncePerSession, first.ConfirmationMode);
        Assert.Equal(["catalog_search", "catalog_get", "call", "call_result", "balance"], first.AllowedTools ?? []);

        // Each call returns a fresh AllowedTools array so mutating one config never leaks.
        Assert.NotSame(first.AllowedTools, second.AllowedTools);
        first.AllowedTools![0] = "mutated";
        Assert.Equal("catalog_search", second.AllowedTools![0]);
    }

    [Fact]
    public void TregPresetRestrictedV2UsesVerifiedCuratedEndpointAndTools()
    {
        var config = TregPreset.RestrictedV2(McpConfirmationMode.EveryCall, title: "treg curated");

        Assert.Equal("https://treg.to/mcp/v2/", config.Url);
        Assert.Equal("treg curated", config.Title);
        Assert.Equal("Authorization", config.HeaderName);
        Assert.Equal("Bearer ", config.HeaderPrefix);
        Assert.Equal(McpConfirmationMode.EveryCall, config.ConfirmationMode);
        Assert.Equal(
            ["catalog_search", "catalog_get", "catalog_call_read", "catalog_call_write", "balance"],
            config.AllowedTools ?? []);
    }

    [Fact]
    public void SkillImportParsesReaMarketingSkillIntoValidatedAgentManifest()
    {
        var file = new SkillFile(
            Name: "skills/rea-marketing/SKILL.md",
            Content: ReaMarketingSkillMarkdown,
            Source: "https://treg.to/rea-marketing");

        var appId = SkillImport.AppIdOf(file);
        var manifest = SkillImport.ToApp(file);

        Assert.Equal("rea-marketing", appId);
        Assert.Equal("Reverse Engineer Holiday Marketing", manifest.Title);
        Assert.Equal(
            "Use when the user wants to reverse engineer a brand's holiday marketing across ads, emails, and landing pages.",
            manifest.Description);
        Assert.Equal("Skills", manifest.Category);
        Assert.Empty(manifest.Operations);
        Assert.Empty(manifest.Settings);

        var slot = Assert.Single(manifest.Slots);
        Assert.Equal("treg", slot.Name);
        Assert.Equal("", slot.Connector);
        Assert.Equal("tool@1", slot.Capability);
        Assert.Equal("tool@1", slot.Binds);
        Assert.True(slot.Required);

        Assert.NotNull(manifest.Requires);
        Assert.Equal(["tool@1"], manifest.Requires.Capabilities ?? []);

        Assert.NotNull(manifest.Agent);
        Assert.StartsWith("# Reverse Engineer Holiday Marketing", manifest.Agent.Instructions, StringComparison.Ordinal);
        Assert.Contains("catalog_search", manifest.Agent.Instructions, StringComparison.Ordinal);
        Assert.Equal(["treg"], manifest.Agent.Tools ?? []);
        Assert.Equal(
            [
                "Reverse engineer Nike's Black Friday campaign",
                "Compare Glossier vs Rhode holiday landing pages",
            ],
            manifest.Agent.Starters ?? []);

        // Digest must be deterministic across repeated imports.
        var digest1 = AppPublicationDigest.Of(new AppBundle(appId, "1.0.0", manifest, string.Empty, new()));
        var digest2 = AppPublicationDigest.Of(new AppBundle(appId, "1.0.0", SkillImport.ToApp(file), string.Empty, new()));
        Assert.Equal(digest1, digest2);
    }

    [Fact]
    public void SkillImportSupportsFrontmatterOverridesInlineArrayAndBlockScalar()
    {
        const string markdown = """
            ---
            name: "competitor-intel"
            title: "Competitor Intel"
            icon: "radar"
            category: "Marketing"
            launch: maximized
            description: >
              Track pricing shifts and ad creative
              across rival SaaS brands.
            starters: ["Audit Linear pricing", "Compare Notion vs Coda"]
            ---

            Always inspect prices with `catalog_get` before calling paid routes.
            """;

        var model = new ModelSelection(Model: "claude-sonnet-4-5", Reasoning: "medium");
        var manifest = SkillImport.ToApp(new SkillFile("competitor-intel.md", markdown), model: model);

        Assert.Equal("Competitor Intel", manifest.Title);
        Assert.Equal("radar", manifest.Icon);
        Assert.Equal("Marketing", manifest.Category);
        Assert.Equal(AppLaunch.Maximized, manifest.Launch);
        Assert.Equal("Track pricing shifts and ad creative across rival SaaS brands.", manifest.Description);
        Assert.NotNull(manifest.Agent);
        Assert.Equal(model, manifest.Agent.Model);
        Assert.Equal(["Audit Linear pricing", "Compare Notion vs Coda"], manifest.Agent.Starters ?? []);
        Assert.Equal("Always inspect prices with `catalog_get` before calling paid routes.", manifest.Agent.Instructions);
    }

    [Fact]
    public void SkillImportSupportsYamlListStartersAndHumanizesSlugWhenNoHeadingOrTitle()
    {
        const string markdown = """
            ---
            name: "seo-site-audit"
            starters:
              - "Audit https://example.com technical SEO"
              - "Find broken internal links"
            ---

            Run a structured technical SEO audit using treg web crawlers.
            """;

        var file = new SkillFile("SKILL.md", markdown);
        var manifest = SkillImport.ToApp(file);

        Assert.Equal("seo-site-audit", SkillImport.AppIdOf(file));
        Assert.Equal("Seo Site Audit", manifest.Title);
        Assert.Equal("Run a structured technical SEO audit using treg web crawlers.", manifest.Description);
        Assert.Equal(
            ["Audit https://example.com technical SEO", "Find broken internal links"],
            manifest.Agent!.Starters ?? []);
    }

    [Fact]
    public void SkillImportAllowsExplicitStarterOverrideAndRejectsEmptyInstructions()
    {
        var manifest = SkillImport.ToApp(
            new SkillFile("skills/rea-marketing/SKILL.md", ReaMarketingSkillMarkdown),
            starters: ["Run a quick Q4 teardown", "Run a quick Q4 teardown", "  "]);

        Assert.Equal(["Run a quick Q4 teardown"], manifest.Agent!.Starters ?? []);

        const string emptyBody = """
            ---
            name: "empty-skill"
            description: "No body"
            ---
            """;

        Assert.Throws<InvalidDataException>(() => SkillImport.ToApp(new SkillFile("empty-skill/SKILL.md", emptyBody)));
        Assert.Throws<InvalidDataException>(() => SkillImport.ToApp(new SkillFile("SKILL.md", "   \n  ")));
    }

    [Fact]
    public void SkillImportDoesNotAutoGrantUntrustedFrontmatterAndValidatesExplicitOwnerOverrides()
    {
        const string untrustedSkill = """
            ---
            name: "sql-analyst"
            handle: "@sql-analyst"
            description: "Analyzes warehouse SQL tables."
            memory_scope: isolated
            allowed_contracts:
              - "IntoChat.OS.Modules.Apps.IData"
              - "DigitalBrain.Capabilities.Secrets.ISecretVault"
            tool_sources: ["other-brain/connection/mcp/sql"]
            ---

            # SQL Analyst

            Run read-only analytical queries and explain findings clearly.
            """;

        // Untrusted SKILL.md frontmatter MUST NOT self-grant AllowedContracts or ToolSources.
        var untrustedManifest = SkillImport.ToApp(new SkillFile("skills/sql-analyst/SKILL.md", untrustedSkill), includeTregSlot: false);
        Assert.Equal("@sql-analyst", untrustedManifest.Agent!.Handle);
        Assert.Equal(MemoryScope.IsolatedBotScope, untrustedManifest.Agent.MemoryScope);
        Assert.Empty(untrustedManifest.Agent.AllowedContracts ?? []);
        Assert.Empty(untrustedManifest.Agent.ToolSources ?? []);

        // Explicit owner-supplied overrides ARE applied and validated fail-closed.
        var ownerGranted = SkillImport.ToApp(
            new SkillFile("skills/sql-analyst/SKILL.md", untrustedSkill),
            allowedContracts: ["IntoChat.OS.Modules.Apps.IData"],
            toolSources: ["local/connection/mcp/sql"],
            includeTregSlot: false);
        Assert.Equal(["IntoChat.OS.Modules.Apps.IData"], ownerGranted.Agent!.AllowedContracts ?? []);
        Assert.Equal(["local/connection/mcp/sql"], ownerGranted.Agent.ToolSources ?? []);

        // Unclosed frontmatter fence fails cleanly.
        Assert.Throws<InvalidDataException>(() =>
            SkillImport.ToApp(new SkillFile("broken/SKILL.md", "---\nname: broken\n# Body without closing fence")));

        // Reserved @ino handle fails cleanly.
        Assert.Throws<InvalidDataException>(() =>
            SkillImport.ToApp(new SkillFile("ino/SKILL.md", "---\nhandle: @ino\n---\nInstructions.")));

        // Explicit disallowed secret contract fails closed.
        Assert.Throws<InvalidDataException>(() =>
            SkillImport.ToApp(
                new SkillFile("bad/SKILL.md", "---\nhandle: @bad-bot\n---\nInstructions."),
                allowedContracts: ["DigitalBrain.Capabilities.Secrets.ISecretVault"]));

        // Explicit malformed tool source fails closed.
        Assert.Throws<InvalidDataException>(() =>
            SkillImport.ToApp(
                new SkillFile("bad/SKILL.md", "---\nhandle: @bad-bot\n---\nInstructions."),
                toolSources: ["not-a-valid-key"]));

        // Unknown memory_scope fails cleanly.
        Assert.Throws<InvalidDataException>(() =>
            SkillImport.ToApp(new SkillFile("bad/SKILL.md", "---\nhandle: @bad-bot\nmemory_scope: host-secrets\n---\nInstructions.")));
    }

    [Fact]
    public async Task AppNeuronRejectsDuplicateSpecialistHandleAndInoAppRejectsMalformedAppId()
    {
        TestOS? osRef = null;
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DigitalBrain.Kernel.IBrainObserver>(_ => osRef!);
        services.AddSingleton<DigitalBrain.Neurons.INeuronProxies>(_ => osRef!.Proxies);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        osRef = os;
        os.RegisterCatalog();
        os.Register<AppNeuron>();
        os.Register<RecordingConversation>();
        os.Register<ChatThreadNeuron>();
        os.Register<IntoChat.OS.Modules.Flutter.Conversations.ThreadDirectoryNeuron>();
        os.Register<IntoChat.OS.Apps.Ino.InoApp>();

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var firstApp = os.Get<IApp>(new NeuronId(Brain, AppGrainType.Name, "apps/first-bot"));
        var firstManifest = AppManifest.ForSpecialist(
            "First Bot",
            "First specialist",
            new AppAgent("Be first.", Handle: "@unique-bot"));
        var created = await firstApp.Create(firstManifest, "");
        Assert.Equal(AppStatus.Live, created.Status);

        // Creating a second AppNeuron claiming the same @handle must fail closed.
        var secondApp = os.Get<IApp>(new NeuronId(Brain, AppGrainType.Name, "apps/second-bot"));
        var duplicateManifest = AppManifest.ForSpecialist(
            "Second Bot",
            "Duplicate handle",
            new AppAgent("Be second.", Handle: "@unique-bot"));
        await Assert.ThrowsAsync<InvalidDataException>(() => secondApp.Create(duplicateManifest, ""));

        // Malformed AppId in InoApp.OpenSpecialist must NOT throw an unhandled exception (records rejection cleanly).
        var ino = os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default"));
        var badArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new IntoChat.OS.Apps.Ino.InoOpenSpecialist("---"),
            typeof(IntoChat.OS.Apps.Ino.InoOpenSpecialist));
        await ino.Dispatch(new Command(".", "ino.open-specialist", badArgs));
        var viewAfterBad = await ino.Read();
        Assert.NotNull(viewAfterBad);
        Assert.Contains("conversations/main", viewAfterBad.View.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportedSkillRunsEndToEndThroughAppNeuronAndUnifiedMessengerInoApp()
    {
        RecordingConversation.Reset();

        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.RegisterCatalog();
        os.Register<AppNeuron>();
        os.Register<RecordingConversation>();
        os.Register<ChatThreadNeuron>();
        os.Register<IntoChat.OS.Modules.Flutter.Conversations.ThreadDirectoryNeuron>();
        os.Register<IntoChat.OS.Apps.Ino.InoApp>();

        using var person = os.Edge.Enter(os.Edge.Person("local", "local", Brain));

        var file = new SkillFile("skills/rea-marketing/SKILL.md", ReaMarketingSkillMarkdown);
        var appId = SkillImport.AppIdOf(file);
        var manifest = SkillImport.ToApp(file);

        var app = os.Get<IApp>(new NeuronId(Brain, AppGrainType.Name, $"apps/{appId}"));
        var created = await app.Create(manifest, "");
        Assert.Equal(AppStatus.Live, created.Status);

        const string tregConnectionKey = "local/connection/mcp/treg";
        var bound = await app.BindSlots(
            new Dictionary<string, string>(StringComparer.Ordinal) { [TregPreset.SlotName] = tregConnectionKey },
            TestContext.Current.CancellationToken);
        Assert.Equal(SlotBindingStatus.Ok, bound.Status);

        var ino = os.Get<IScreen>(new NeuronId(Brain, "app.ino", "default"));
        var openArgs = DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(
            new IntoChat.OS.Apps.Ino.InoOpenSpecialist(appId),
            typeof(IntoChat.OS.Apps.Ino.InoOpenSpecialist));
        await ino.Dispatch(new Command(".", "ino.open-specialist", openArgs));
        var published = await ino.Read();

        Assert.NotNull(published);
        Assert.Equal("app.ino", published.Alias);
        var rawView = published.View.GetRawText();
        Assert.Contains("Reverse Engineer Holiday Marketing", rawView, StringComparison.Ordinal);
        Assert.Contains("Compare Glossier vs Rhode holiday landing pages", rawView, StringComparison.Ordinal);
        Assert.Contains("Reverse engineer Nike\\u0027s Black Friday campaign", rawView, StringComparison.Ordinal);

        var seated = Assert.Single(RecordingConversation.SeatedCalls, s => s.AgentId == $"agent-{appId}");
        Assert.Equal(manifest.Agent!.Instructions, seated.Definition.Instructions);
        Assert.Empty(seated.Definition.Contracts);
        Assert.False(seated.Definition.IncludeComponentOperations);
        Assert.Equal([tregConnectionKey], seated.Definition.ToolSources ?? []);

        await ino.Click("starter:0");

        Assert.Equal(["Reverse engineer Nike's Black Friday campaign"], RecordingConversation.SaidTexts);

        var childView = await ino.ReadChild(IntoChat.OS.Apps.Ino.InoView.ConversationKey);
        Assert.NotNull(childView);
        var threadView = Assert.IsType<ChatThreadView>(
            childView.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web));
        Assert.Equal(2, threadView.Messages.Count);
        Assert.Equal("Reverse engineer Nike's Black Friday campaign", threadView.Messages[0].Text);
        Assert.Equal("Teardown: Reverse engineer Nike's Black Friday campaign", threadView.Messages[1].Text);
    }

    [Neuron(IConversation.Type)]
    private sealed class RecordingConversation : Neuron, IConversation
    {
        private readonly List<ConversationEntry> _entries = [];
        private readonly List<AgentRun> _runs = [];
        private long _revision;

        public static List<(string AgentId, AgentDefinition Definition)> SeatedCalls { get; } = [];
        public static List<string> SaidTexts { get; } = [];

        public static void Reset()
        {
            SeatedCalls.Clear();
            SaidTexts.Clear();
        }

        public Task Seat(string agentId, AgentDefinition definition)
        {
            SeatedCalls.Add((agentId, definition));
            return Task.CompletedTask;
        }

        public Task Configure(ThreadTrustPosture posture, ThreadKind kind, string? title = null, string? leadAgentId = null) => Task.CompletedTask;

        public async Task Say(string text, ModelId? model = null, AttachmentRef[]? attachments = null, string? messageId = null, EntryId? replyTo = null)
        {
            SaidTexts.Add(text);
            var runId = $"r{_runs.Count + 1}";
            var now = DateTimeOffset.UtcNow;
            var userEntry = new Said(EntryIds.Said(runId), new Participant.Human(new AccountId("local")), now, text);
            var replyEntry = new Said(EntryIds.Reply(runId), new Participant.Assistant("agent-rea-marketing"), now.AddMilliseconds(5), $"Teardown: {text}");
            _entries.Add(userEntry);
            _entries.Add(replyEntry);
            _runs.Add(new AgentRun(runId, AgentRunStatus.Completed, text, now, now.AddMilliseconds(5), $"Teardown: {text}"));
            _revision++;
            await Publish(new ConversationChanged(ConversationId.Parse(Id.Key["conversations/".Length..]), _revision));
        }

        public Task Stop() => Task.CompletedTask;
        public Task Regenerate(EntryId reply) => Task.CompletedTask;
        public Task Invite(AccountId friend, BrainId? friendBrain = null, DigitalBrain.Components.Role role = DigitalBrain.Components.Role.Editor) => Task.CompletedTask;
        public Task Pin(string screenType, string screenKey = "default") => Task.CompletedTask;
        public Task<DigitalBrain.Units.PublishedView> Act(string screenType, string screenKey, DigitalBrain.UI.Command command) => throw new NotSupportedException();
        public Task Here() => Task.CompletedTask;
        public Task Leave() => Task.CompletedTask;
        public Task React(EntryId target, string emoji, bool remove = false) => Task.CompletedTask;
        public Task Edit(EntryId target, string text) => Task.CompletedTask;
        public Task Delete(EntryId target) => Task.CompletedTask;
        public Task Post(string text, string id, Visibility visibility = Visibility.Everyone) => Task.CompletedTask;
        public Task Voiced(string heard, string reply, ModelId model, string id) => Task.CompletedTask;

        public Task<ConversationView> Read(long? sinceRevision = null)
        {
            var convId = ConversationId.Parse(Id.Key["conversations/".Length..]);
            return Task.FromResult(new ConversationView(
                convId,
                _revision,
                [],
                [.. _entries],
                null,
                [.. _runs],
                false,
                []));
        }
    }
}
