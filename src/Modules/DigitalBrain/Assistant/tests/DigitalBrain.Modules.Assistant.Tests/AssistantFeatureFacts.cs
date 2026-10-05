using System.Text;
using System.Globalization;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.Assistant;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Flutter;
using ChatRole = DigitalBrain.Flutter.Chat.ChatRole;
using DigitalBrain.Flutter.Surface;
using DigitalBrain.Flutter.VoiceInput;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Supabase;
using DigitalBrain.Supabase.Tables;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DigitalBrain.Modules.Assistant.Tests;

public sealed class AssistantFeatureFacts
{
    private static async Task<ModuleBrain> StartBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        return await ModuleTest.Create().WithExecution(new TestExecutionOptions
        {
            PrivateConfiguration = new Dictionary<string, string?> { [IntegrationSchema.Key("openai", "ApiKey")] = "test-no-network", [IntegrationSchema.Key("supabase", "ConnectionString")] = "Host=localhost;Database=fixture;Username=test;Password=test" },
        })
            .WithModule<AssistantModule>().RequireModules([new DigitalBrain.Apps.AppsModule(), new DigitalBrain.AI.AIModule(), new DigitalBrain.Compute.ComputeModule(), new DigitalBrain.Flutter.FlutterModule()])
            .WithModule<SupabaseModule>()
            .ConfigureSilo(silo => silo.Services
                .Configure<AIOptions>(options => { options.Default.Provider = "OpenAI"; options.Default.Model = "fixture"; options.Default.Capabilities = LlmCapabilities.Tools; })
                .AddSingleton<InjectedModelTurnRunner>()
                .AddSingleton<IAgentTurnRunner>(services => services.GetRequiredService<InjectedModelTurnRunner>())
                .AddSingleton<IChatClient>(new ScriptedAssistantModel())
                .AddSingleton<CustomersDatabase>()
                .AddSingleton<ISupabaseProvider>(services => services.GetRequiredService<CustomersDatabase>())
                .AddSingleton<IAudioTranscriptionService>(new TextTranscriber()))
            .StartAsync(ct);
    }

    private const string Key = "assistant-facts/applications/assistant";

    [Fact]
    public async Task TheAssistantComposesItsChat()
    {
        await using var brain = await StartBrain();
        await brain.Get<IAssistant>(Key).Activate();
        Assert.Equal("Assistant", (await brain.Get<ISurface>(UiParts.NameOf(Key, "surface")).Read()).Definition.Title);
        await new AssistantUiProbe(brain.Grains, Key).AssertExplicitControls();
    }

    [Fact]
    public async Task TypedMessagesAndRepliesAreVisible()
    {
        await using var brain = await StartBrain();
        await brain.Get<IAssistant>(Key).Activate();
        await new AssistantUiProbe(brain.Grains, Key).Submit("hello");
        await Shows(brain, ChatRole.User, "hello");
        await Shows(brain, ChatRole.Assistant, "You said: hello");
    }

    [Fact]
    public async Task VoiceBecomesADraftThatCanBeSent()
    {
        await using var brain = await StartBrain();
        await brain.Get<IAssistant>(Key).Activate();
        var payload = Encoding.UTF8.GetBytes("hello there");
        var wav = new byte[44 + payload.Length];
        "RIFF"u8.CopyTo(wav);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        payload.CopyTo(wav, 44);
        await brain.Get<IVoiceInput>(UiParts.NameOf(Key, AssistantSurface.VoicePart)).Capture(wav, "audio/wav");
        var ui = new AssistantUiProbe(brain.Grains, Key);
        await ui.WaitForDraft("hello there");
        await ui.Submit();
        await Shows(brain, ChatRole.User, "hello there");
        await Shows(brain, ChatRole.Assistant, "You said: hello there");
    }

    [Fact]
    public async Task SupabaseCustomersOpenInATableWindow()
    {
        await using var brain = await StartBrain();
        var database = brain.SiloServices.GetRequiredService<CustomersDatabase>();
        database.Table = "customers";
        database.Rows = [.. Enumerable.Range(1, 3).Select(id => new SupabaseTableRow("row-" + id,
            [id.ToString(CultureInfo.InvariantCulture), $"\"Customer {id}\""]))];
        var app = brain.Get<IAssistant>(Key);
        await app.Configure(new AgentDefinition
        {
            Instructions = "Use the fixture's Supabase tables.",
            Tools = ["supabase_schema", "show_supabase_query_table", "table_read", "table_refine"]
        });
        await app.Activate();
        await new AssistantUiProbe(brain.Grains, Key).Submit("show me customers from supabase");
        await Shows(brain, ChatRole.Assistant, "Your customers are open in a table window.");
        var completed = brain.SiloServices.GetRequiredService<InjectedModelTurnRunner>().CompletedTools.ToArray();
        Assert.Contains("supabase_schema", completed);
        Assert.Contains("show_supabase_query_table", completed);
        var windows = (await brain.Get<IWorkspace>("assistant-facts").Read()).Windows;
        var table = Assert.Single(windows, window => window.IsOpen && window.Reference.Kind == WindowReference.TableKind);
        Assert.Equal(3, (await brain.Get<ISupabaseTable>(table.Reference.NeuronId).Read(new(0, 50)))?.Rows.Length);
    }

    [Fact]
    public async Task ReopeningPreservesMessagesAndDraft()
    {
        await using var brain = await StartBrain();
        var app = brain.Get<IAssistant>(Key);
        var ui = new AssistantUiProbe(brain.Grains, Key);
        await app.Activate();
        await ui.Submit("hello");
        await Shows(brain, ChatRole.Assistant, "You said: hello");
        await ui.Input("unfinished");
        await app.Activate();
        await Shows(brain, ChatRole.Assistant, "You said: hello");
        await ui.WaitForDraft("unfinished");
    }

    [Fact]
    public async Task ReopeningPreservesConversationsWithoutLeakingAcrossThreadsOrWorkspaces()
    {
        await using var brain = await StartBrain();
        var ct = TestContext.Current.CancellationToken;
        var app = brain.Get<IAssistant>(Key);
        await app.Activate();
        var conversation = await app.Conversation("thread-1");
        await conversation.BeginConversation(new("run-1", "question"), ct);
        await conversation.CompleteConversation(new("run-1", "question", "saved answer", []), ct);
        await app.Activate();
        Assert.Equal("saved answer", Assert.Single((await (await app.Conversation("thread-1")).ReadConversation(ct)).Turns).AssistantText);
        Assert.Empty((await (await app.Conversation("thread-2")).ReadConversation(ct)).Turns);
        var other = brain.Get<IAssistant>(AssistantSurface.Key("assistant-facts-other"));
        Assert.Empty((await (await other.Conversation("thread-1")).ReadConversation(ct)).Turns);
    }

    [Fact]
    public async Task ConfigurationSurvivesRepeatedStartup()
    {
        await using var brain = await StartBrain();
        var app = brain.Get<IAssistant>(Key);
        await app.Activate();
        await app.Configure(new AgentDefinition { DisplayName = "Custom assistant", Instructions = "Be concise." });
        await app.Activate();
        Assert.Equal("Be concise.", (await app.DefineTurn(null)).Instructions);
    }

    [Fact]
    public async Task TurnsRetainSummaryAndRejectInvalidConversationIdentifiers()
    {
        await using var brain = await StartBrain();
        var app = brain.Get<IAssistant>(Key);
        await app.Activate();
        Assert.Contains("Earlier conversation summary: Earlier work", (await app.DefineTurn("Earlier work")).Instructions, StringComparison.Ordinal);
        foreach (var invalid in new[] { "", "foreign/thread", "foreign\\thread", new string('x', 201) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => app.Conversation(invalid));
        }
    }

    private static async Task Shows(ModuleBrain brain, ChatRole role, string text)
    {
        var ui = new AssistantUiProbe(brain.Grains, Key);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var thread = await ui.Read();
            if (thread.Messages.Any(message => message.Role == role && message.Text == text)
                && (await ui.VisibleText()).Contains(text, StringComparison.Ordinal)) { return; }
            Assert.True(thread.Error is null && DateTimeOffset.UtcNow < deadline,
                $"State: {System.Text.Json.JsonSerializer.Serialize(thread)}. Runner: {string.Join("; ", brain.SiloServices.GetRequiredService<InjectedModelTurnRunner>().Failures)}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    // Audio in these scenarios is the UTF-8 text the user "said".
    private sealed class TextTranscriber : IAudioTranscriptionService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool InitializationFailed => false;
        public string? ErrorMessage => null;
        public string ModelId => "text";
        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<string> TranscribeAsync(Stream audioStream, string fileName, CancellationToken cancellationToken = default)
        {
            audioStream.Position = 44; // The fixture stores its transcript after a WAV header.
            using var reader = new StreamReader(audioStream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }
}
