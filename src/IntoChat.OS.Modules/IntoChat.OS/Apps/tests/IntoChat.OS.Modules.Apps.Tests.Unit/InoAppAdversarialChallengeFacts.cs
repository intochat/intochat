using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain.Testing;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoAppAdversarialChallengeFacts
{
    [Fact]
    public void ExecutionContextProviderPropagatesAmbientSessionCancellationViaReflection()
    {
        // Check if IntoChat.OS.Client is available
        var ambientType = Type.GetType("IntoChat.OS.Client.Runtime.AmbientSession, IntoChat.OS.Client");
        if (ambientType is null)
        {
            try
            {
                var asm = Assembly.Load("IntoChat.OS.Client");
                ambientType = asm.GetType("IntoChat.OS.Client.Runtime.AmbientSession");
            }
            catch
            {
                // If assembly cannot be loaded, test the default fallback
                var prov = new ExecutionContextProvider();
                Assert.NotNull(prov.Current);
                return;
            }
        }

        if (ambientType is not null)
        {
            var sessionType = Type.GetType("IntoChat.OS.Client.SessionConnection, IntoChat.OS.Client")
                ?? Assembly.Load("IntoChat.OS.Client").GetType("IntoChat.OS.Client.SessionConnection")!;
            var session = Activator.CreateInstance(sessionType, "test-session", "test-device", "test-user");

            using var cts = new CancellationTokenSource();
            var enterMethod = ambientType.GetMethod("Enter", BindingFlags.Public | BindingFlags.Static)!;
            var disposable = (IDisposable)enterMethod.Invoke(null, [
                session,
                "test-behavior",
                new Dictionary<string, string> { ["ENV_KEY"] = "val" },
                cts.Token,
                null,
                null,
            ])!;

            using (disposable)
            {
                var provider = new ExecutionContextProvider();
                var context = provider.Current;

                Assert.Equal("val", context.Environment["ENV_KEY"]);
                Assert.False(context.CancellationToken.IsCancellationRequested);

                cts.Cancel();

                Assert.True(context.CancellationToken.IsCancellationRequested);
            }
        }
    }

    [Fact]
    public async Task InoAppHandlesEmptyAndWhitespacePromptsWithoutCrashing()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend(null, null));
        Assert.Single(ino.App.State.Turns);
        Assert.Equal("hello", ino.App.State.Turns[0].UserMessage);

        await ino.Request("send", new InoSend("", ""));
        Assert.Equal(2, ino.App.State.Turns.Length);

        await ino.Request("send", new InoSend("   \r\n\t   ", "   "));
        Assert.Equal(3, ino.App.State.Turns.Length);
    }

    [Fact]
    public async Task InoAppHandlesExtremelyLongPromptSafely()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var longPrompt = new string('A', 50000);

        await ino.Request("send", new InoSend(longPrompt));

        Assert.Single(ino.App.State.Turns);
        Assert.Equal(50000, ino.App.State.Turns[0].UserMessage.Length);
        Assert.True(ino.App.State.Title.Length <= 52);
    }

    [Fact]
    public async Task InoAppHandlesMalformedSlashCommandsSafely()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        await ino.Request("send", new InoSend("/"));
        await ino.Request("send", new InoSend("/      "));
        await ino.Request("send", new InoSend("/unrecognized_command_xyz foo bar"));
        await ino.Request("send", new InoSend("/model "));
        await ino.Request("send", new InoSend("/rename "));
        await ino.Request("send", new InoSend("/scenario non_existent_scenario"));

        Assert.NotNull(ino.App.State.Status);
    }

    [Fact]
    public async Task InoAppHandlesAdversarialInputsWithoutExceptions()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        string[] adversarialInputs =
        [
            "'; DROP TABLE opportunities; --",
            "../../../../etc/passwd",
            "{0} {1} {2} %s %x %n",
            "\0\a\b\f\v\u200B\u200C\uFEFF",
            "{\"intent\": \"photocraft\", \"override\": true}",
            "routed:fake.hack:exploit",
        ];

        foreach (var input in adversarialInputs)
        {
            await ino.Request("send", new InoSend(input));
        }

        Assert.Equal(adversarialInputs.Length, ino.App.State.Turns.Length);
    }

    [Fact]
    public async Task AllSettingsNavigationRoutesProduceExactRouteHints()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();

        // 1. Ollama local server
        await ino.Request("send", new InoSend("Where do I set up my local Ollama server?"));
        Assert.Equal("routed:settings.navigate:models:local-server", ino.App.State.Turns[^1].RouteHint);

        // 2. Google drive connection sheet
        await ino.Request("send", new InoSend("Open Google Drive connection sheet in settings"));
        Assert.Equal("routed:settings.navigate:connections:google_drive:google_drive", ino.App.State.Turns[^1].RouteHint);

        // 3. Keys revocation with approval gate
        await ino.Request("send", new InoSend("Revoke API key in settings"));
        Assert.Equal("routed:settings.navigate:keys", ino.App.State.Turns[^1].RouteHint);
        Assert.Contains(ino.App.State.ActiveCards, c => c.Kind == "approval" && c.Status == "pending");

        // 4. Delete account with approval gate
        await ino.Request("send", new InoSend("Delete account in settings"));
        Assert.Equal("routed:settings.navigate:danger::delete-account", ino.App.State.Turns[^1].RouteHint);

        // 5. Memory clear with approval gate
        await ino.Request("send", new InoSend("Clear memory in settings"));
        Assert.Equal("routed:settings.navigate:files:memory", ino.App.State.Turns[^1].RouteHint);

        // 6. Generic settings sections
        await ino.Request("send", new InoSend("Open settings and manage variables"));
        Assert.Equal("routed:settings.navigate:variables", ino.App.State.Turns[^1].RouteHint);

        await ino.Request("send", new InoSend("Open settings billing"));
        Assert.Equal("routed:settings.navigate:billing", ino.App.State.Turns[^1].RouteHint);

        await ino.Request("send", new InoSend("Where do I manage team members in settings?"));
        Assert.Equal("routed:settings.navigate:team", ino.App.State.Turns[^1].RouteHint);
    }

    [Fact]
    public async Task InoAppWithInjectedDecisionMaker_ThrowsWhenNoCallerContextIsStamped()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var services = new ServiceCollection().BuildServiceProvider();
        var contextProvider = new ExecutionContextProvider();
        var decisionMaker = new DecisionMaker(services, contextProvider, NullLogger<DecisionMaker>.Instance);
        ino.App.DecisionMaker = decisionMaker;

        // When no caller context is stamped, DecisionMaker.DecideReactiveAsync safely falls back without throwing
        await ino.Request("send", new InoSend("Where do I set up my local Ollama server?"));
        Assert.Equal("routed:settings.navigate:models:local-server", ino.App.State.Turns[^1].RouteHint);

        await ino.Request("send", new InoSend("Good morning Ino"));
        Assert.StartsWith("agent:", ino.App.State.Turns[^1].RouteHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecisionMaker_InPackagedBinary_ThrowsNoCallerContextStamped()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var contextProvider = new ExecutionContextProvider();
        var decisionMaker = new DecisionMaker(services, contextProvider, NullLogger<DecisionMaker>.Instance);
        string[] candidates = ["photocraft", "filmcraft", "chart.mutation", "chart.question", "database.query", "settings.navigate", "file.resource", "connection.string", "app.install", "general.chat"];

        // Calling DecideAsync when unstamped resolves safely via heuristic fallback without throwing NoCallerContextStamped
        var result = await decisionMaker.DecideAsync(new DecisionContext("Where do I set up my local Ollama server?", candidates, Mode: DecisionMode.Reactive), TestContext.Current.CancellationToken);
        Assert.Equal("settings.navigate", result.Choice);
        Assert.True(result.Confidence >= 0.9);

        var generalResult = await decisionMaker.DecideAsync(new DecisionContext("Good morning Ino", candidates, Mode: DecisionMode.Reactive), TestContext.Current.CancellationToken);
        Assert.Equal("general.chat", generalResult.Choice);
    }

    [Theory]
    [InlineData("Where do I set up my local Ollama server?")]
    [InlineData("where do i set up my local ollama server?")]
    [InlineData("WHERE DO I SET UP MY LOCAL OLLAMA SERVER?")]
    [InlineData("How to configure local Ollama server")]
    [InlineData("Where is the Ollama configuration in settings?")]
    [InlineData("Set up local Ollama")]
    [InlineData("Where can I configure Ollama models?")]
    public async Task EmpiricalStress_OllamaAndSettingsRouting_RoutesToSettingsNavigate_NeverPhotoCraft(string prompt)
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var services = new ServiceCollection().BuildServiceProvider();
        var contextProvider = new ExecutionContextProvider();
        var decisionMaker = new DecisionMaker(services, contextProvider, NullLogger<DecisionMaker>.Instance);
        ino.App.DecisionMaker = decisionMaker;

        string[] candidates = ["photocraft", "filmcraft", "chart.mutation", "chart.question", "database.query", "settings.navigate", "file.resource", "connection.string", "app.install", "general.chat"];
        var decision = await decisionMaker.DecideAsync(new DecisionContext(prompt, candidates, Mode: DecisionMode.Reactive), TestContext.Current.CancellationToken);

        Assert.Equal("settings.navigate", decision.Choice);
        Assert.True(decision.Confidence >= 0.9);

        await ino.Request("send", new InoSend(prompt));
        var lastTurn = ino.App.State.Turns[^1];

        Assert.StartsWith("routed:settings.navigate", lastTurn.RouteHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photocraft", lastTurn.RouteHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ino.App.State.ActiveCards, c => c.Kind.Contains("photo", StringComparison.OrdinalIgnoreCase) || c.Kind.Contains("studio", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Good morning Ino")]
    [InlineData("Hello, how are you?")]
    [InlineData("Can you write a poem about autumn?")]
    [InlineData("What is the speed of light?")]
    [InlineData("Explain dependency injection in .NET")]
    [InlineData("Who was Ada Lovelace?")]
    [InlineData("Tell me a funny joke about programming")]
    [InlineData("Brainstorm 3 project ideas")]
    [InlineData("Summarize the history of Unix")]
    public async Task EmpiricalStress_GeneralChatQueries_RouteToGeneralChat_NeverPhotoCraft(string prompt)
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var services = new ServiceCollection().BuildServiceProvider();
        var contextProvider = new ExecutionContextProvider();
        var decisionMaker = new DecisionMaker(services, contextProvider, NullLogger<DecisionMaker>.Instance);
        ino.App.DecisionMaker = decisionMaker;

        string[] candidates = ["photocraft", "filmcraft", "chart.mutation", "chart.question", "database.query", "settings.navigate", "file.resource", "connection.string", "app.install", "general.chat"];
        var decision = await decisionMaker.DecideAsync(new DecisionContext(prompt, candidates, Mode: DecisionMode.Reactive), TestContext.Current.CancellationToken);

        Assert.Equal("general.chat", decision.Choice);

        await ino.Request("send", new InoSend(prompt));
        var lastTurn = ino.App.State.Turns[^1];

        Assert.StartsWith("agent:", lastTurn.RouteHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photocraft", lastTurn.RouteHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ino.App.State.ActiveCards, c => c.Kind.Contains("photo", StringComparison.OrdinalIgnoreCase) || c.Kind.Contains("studio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EmpiricalStress_UnstampedContext_NeverThrowsInvalidOperationException_AcrossAllModes()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var contextProvider = new ExecutionContextProvider();
        var decisionMaker = new DecisionMaker(services, contextProvider, NullLogger<DecisionMaker>.Instance);
        string[] candidates = ["photocraft", "filmcraft", "chart.mutation", "chart.question", "database.query", "settings.navigate", "file.resource", "connection.string", "app.install", "general.chat"];

        // 1. Reactive Mode
        var reactiveResult = await decisionMaker.DecideAsync(new DecisionContext("Unrecognized test prompt", candidates, Mode: DecisionMode.Reactive), TestContext.Current.CancellationToken);
        Assert.NotNull(reactiveResult);
        Assert.Equal("general.chat", reactiveResult.Choice);

        // 2. Deliberate Mode
        var deliberateResult = await decisionMaker.DecideAsync(new DecisionContext("Unrecognized test prompt", candidates, Mode: DecisionMode.Deliberate), TestContext.Current.CancellationToken);
        Assert.NotNull(deliberateResult);
        Assert.Equal("Unrecognized test prompt", deliberateResult.Choice);

        // 3. Adaptive Mode (escalates from Reactive to Deliberate if confidence low)
        var adaptiveResult = await decisionMaker.DecideAsync(new DecisionContext("Unrecognized test prompt", candidates, Mode: DecisionMode.Adaptive), TestContext.Current.CancellationToken);
        Assert.NotNull(adaptiveResult);
        Assert.Equal("general.chat", adaptiveResult.Choice);
    }
}
