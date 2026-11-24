using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.AgentApp;
using IntoChat.OS.Apps.Controls;
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
    public async Task ImportedSkillRunsEndToEndThroughAppNeuronAndAgentAppScreen()
    {
        RecordingConversation.Reset();

        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider());
        os.Register<AppNeuron>();
        os.Register<RecordingConversation>();
        os.Register<ChatThreadNeuron>();
        os.Register<AgentAppScreen>();

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

        var screen = os.Get<IScreen>(new NeuronId(Brain, "agentapp", appId));
        var published = await screen.Read();

        Assert.NotNull(published);
        Assert.Equal("agentapp", published.Alias);
        var rawView = published.View.GetRawText();
        Assert.Contains("Reverse Engineer Holiday Marketing", rawView, StringComparison.Ordinal);
        Assert.Contains("Compare Glossier vs Rhode holiday landing pages", rawView, StringComparison.Ordinal);
        Assert.Contains("Reverse engineer Nike\\u0027s Black Friday campaign", rawView, StringComparison.Ordinal);

        var seated = Assert.Single(RecordingConversation.SeatedCalls);
        Assert.Equal($"agent-{appId}", seated.AgentId);
        Assert.Equal(manifest.Agent!.Instructions, seated.Definition.Instructions);
        Assert.Empty(seated.Definition.Contracts);
        Assert.False(seated.Definition.IncludeComponentOperations);
        Assert.Equal([tregConnectionKey], seated.Definition.ToolSources ?? []);

        await screen.Activate("agentapp", published.Generation, "starter:0", null, "local");

        Assert.Equal(["Reverse engineer Nike's Black Friday campaign"], RecordingConversation.SaidTexts);

        var childView = await screen.ReadChild(AgentAppScreen.ConversationSlotKey);
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

        public async Task Say(string text, ModelId? model = null)
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
        public Task Invite(AccountId friend) => Task.CompletedTask;
        public Task Post(string text, string id) => Task.CompletedTask;
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
