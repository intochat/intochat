using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Screens;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Connections.Platform.Entries;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Apps.Tests.Integration.Ino;

public sealed class InoWiringFacts(InoSiloFixture fixture) : IClassFixture<InoSiloFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ISendAMessageAndSeeItThenTheReply()
    {
        var brain = fixture.NewBrain("sendreply");
        using var _ = fixture.AsPerson(brain);
        var held = fixture.Model.Hold("Hi Vlad.");

        var screen = fixture.InoScreen(brain);
        await OpenInoAsync(screen);

        await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "hello");
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(8), Ct);

        // Immediately while working: user's "hello" is visible inside Conversation and composer is disabled!
        var whileWorking = await fixture.WaitForScreenAsync(
            screen,
            v => PresentedView.From(v).Texts.Contains("hello") && !InoSiloFixture.RequireConversation(v).Composer.Enabled,
            Ct);
        var workingConv = InoSiloFixture.RequireConversation(whileWorking);
        Assert.Equal("working", workingConv.Status);
        Assert.Contains(workingConv.Messages, m => m.Role == "user" && m.Text == "hello");
        Assert.NotNull(workingConv.Stop);

        // Release the LLM response -> "Hi Vlad." appears and composer re-enables!
        held.Release();

        var completed = await fixture.WaitForScreenAsync(
            screen,
            v =>
            {
                var texts = PresentedView.From(v).Texts;
                return texts.Contains("hello") && texts.Contains("Hi Vlad.") && InoSiloFixture.RequireConversation(v).Composer.Enabled;
            },
            Ct);

        var doneConv = InoSiloFixture.RequireConversation(completed);
        Assert.Equal("ready", doneConv.Status);
        Assert.Equal([("user", "hello"), ("assistant", "Hi Vlad.")], doneConv.Messages.Select(m => (m.Role, m.Text)));
        Assert.Null(doneConv.Error);
    }

    [Fact]
    public async Task AProviderFailureShowsALineAndTryingAgainWorks()
    {
        var brain = fixture.NewBrain("failretry");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.Fail(new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable));

        var screen = fixture.InoScreen(brain);
        await OpenInoAsync(screen);

        await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "hello");

        // The failed reply says why and offers a retry (or, when no reply was started, the error line under the thread does).
        var failedView = await fixture.WaitForScreenAsync(
            screen,
            v => InoSiloFixture.RequireConversation(v) is var c && (c.Error is not null || c.Messages.Any(m => m.Error is not null)),
            Ct);

        var failedConv = InoSiloFixture.RequireConversation(failedView);
        var failedReply = failedConv.Messages.LastOrDefault(m => m.Error is not null);
        Assert.Equal("The model provider did not answer.", failedReply?.Error?.What ?? failedConv.Error!.What);
        Assert.Equal("Try again", failedReply?.Retry?.Label ?? failedConv.Error!.Action!.Label);

        // Now fix the provider and click "Try again".
        fixture.Model.Reply("Hi, back online.");
        await fixture.ActOnThreadAsync(brain, failedReply?.Retry?.Key ?? ChatThreadNeuron.ErrorRetryKey);

        var recovered = await fixture.WaitForScreenAsync(
            screen,
            v => PresentedView.From(v).Texts.Contains("Hi, back online.") && InoSiloFixture.RequireConversation(v).Error is null,
            Ct);

        var recoveredConv = InoSiloFixture.RequireConversation(recovered);
        Assert.Null(recoveredConv.Error);
        Assert.Contains(recoveredConv.Messages, m => m.Role == "assistant" && m.Text == "Hi, back online.");
    }

    [Fact]
    public async Task TheConversationSurvivesReopenAndRefresh()
    {
        var brain = fixture.NewBrain("survive");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.Reply("Still here.");

        var screen = fixture.InoScreen(brain);
        await OpenInoAsync(screen);
        await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "remember this turn");

        await fixture.WaitForScreenAsync(
            screen,
            v => PresentedView.From(v).Texts.Contains("Still here."),
            Ct);

        // Re-open the app on a fresh screen proxy and verify transcript persists via ChatNeuron/AgentState!
        await screen.Request("ino.open", JsonSerializer.SerializeToElement(new InoOpen()));
        var reopened = await fixture.WaitForScreenAsync(
            screen,
            v =>
            {
                var texts = PresentedView.From(v).Texts;
                return texts.Contains("remember this turn") && texts.Contains("Still here.");
            },
            Ct);

        var conv = InoSiloFixture.RequireConversation(reopened);
        Assert.Equal(2, conv.Messages.Count);
    }

    [Fact]
    public async Task ASecondSendWhileAReplyIsRunningStartsNothing()
    {
        var brain = fixture.NewBrain("secondsend");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.ClearRequests();
        var held = fixture.Model.Hold("Single answer.");

        var screen = fixture.InoScreen(brain);
        await OpenInoAsync(screen);

        await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "first question");
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(8), Ct);

        // A second send through the composer while the first run is active: the thread refuses it in order (Ino said it cannot take a
        // message), so it never reaches Ino. A one-way request to Ino could land after the run ends, when sending is allowed again.
        var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "second question"));
        Assert.Equal(ControlProps.Disabled, refused.Reason);

        held.Release();
        var finished = await fixture.WaitForScreenAsync(
            screen,
            v => PresentedView.From(v).Texts.Contains("Single answer."),
            Ct);

        Assert.Single(fixture.Model.Requests);
        Assert.DoesNotContain("second question", PresentedView.From(finished).Texts);
    }

    [Fact]
    public async Task AnEmptyMessageSendsNothing()
    {
        var brain = fixture.NewBrain("emptysend");
        using var _ = fixture.AsPerson(brain);
        fixture.Model.ClearRequests();
        fixture.Model.Reply("Should never be called.");

        var screen = fixture.InoScreen(brain);
        await OpenInoAsync(screen);

        await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "   ");
        var after = await fixture.ReadComposedAsync(screen);

        Assert.Empty(fixture.Model.Requests);
        Assert.NotNull(after);
        Assert.Empty(InoSiloFixture.RequireConversation(after!).Messages);
    }

    [Fact]
    public async Task NoProviderOffersDetectedLocalRtxModelAndSaveProviderPersistsToBrainEntries()
    {
        var brain = fixture.NewBrain("d1setup");
        using var _ = fixture.AsPerson(brain);
        fixture.LocalOllama.Current = new LocalOllamaDetection(
            "http://127.0.0.1:11434",
            "qwen2.5-coder:14b",
            ["qwen2.5-coder:14b", "qwen3.5:4b", "nomic-embed-text:latest"],
            HasNomicEmbed: true);
        fixture.Model.Fail(new NoProviderConfiguredException("No AI provider configured."));

        try
        {
            var screen = fixture.InoScreen(brain);
            await OpenInoAsync(screen);

            await fixture.ActOnThreadAsync(brain, ChatThreadNeuron.ComposerKey, "Write a unit test");

            var noProviderView = await fixture.WaitForScreenAsync(
                screen,
                v => InoSiloFixture.RequireConversation(v).Error?.What == "Ino has no model to answer with.",
                Ct);

            var noProviderTexts = PresentedView.From(noProviderView).Texts;
            Assert.Contains("Use local model (qwen2.5-coder:14b)", noProviderTexts);

            // Now supply an OpenAI key via the inline D1 credential field and verify it writes to durable IBrainEntries AND retries!
            fixture.Model.Reply("Answered after saving key.");
            for (var attempt = 0; ; attempt++)
            {
                var current = await screen.Read() ?? noProviderView;
                try
                {
                    await screen.Activate("app.ino", current.Generation, InoView.ProviderCredentialKey, "sk-proj-testkey1234567890", brain);
                    break;
                }
                catch (ActivationRefusedException) when (attempt < 5)
                {
                    await Task.Delay(25, Ct);
                }
            }

            var afterKeyView = await fixture.WaitForScreenAsync(
                screen,
                v => PresentedView.From(v).Texts.Contains("Answered after saving key."),
                Ct);

            Assert.Null(InoSiloFixture.RequireConversation(afterKeyView).Error);

            // Verify durable IBrainEntries stored the sealed secret entry (never plaintext in summary)!
            var entries = await fixture.Grains.GetGrain<IBrainEntries>(brain).List();
            var openaiEntry = Assert.Single(entries, e => e.Name == BrainEntries.ProviderEntry("openai"));
            Assert.Equal(EntryKind.Secret, openaiEntry.Kind);
            Assert.True(openaiEntry.IsSet);
            Assert.Null(openaiEntry.Value);
            Assert.Equal("7890", openaiEntry.Tail);
        }
        finally
        {
            fixture.LocalOllama.Current = null;
        }
    }

    [Fact]
    public async Task LocalOllamaProbeTimesOutWithinBudgetWhenServerHangs()
    {
        var prev = Environment.GetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE");
        Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }, Ct);

        try
        {
            var probe = new HttpLocalOllamaProbe(null, TimeProvider.System, new Uri($"http://127.0.0.1:{port}"));
            var result = await probe.Detect(Ct);
            Assert.Null(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", prev);
            listener.Stop();
            try { await acceptTask; } catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task LocalOllamaProbeReadsLiveTagsEndpoint()
    {
        var prev = Environment.GetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE");
        Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serveTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await using var stream = client.GetStream();
            var buf = new byte[1024];
            _ = await stream.ReadAsync(buf, Ct);
            const string body = """{"models":[{"name":"qwen2.5-coder:14b"},{"name":"nomic-embed-text:latest"}]}""";
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), Ct);
        }, Ct);

        try
        {
            var probe = new HttpLocalOllamaProbe(null, TimeProvider.System, new Uri($"http://127.0.0.1:{port}"));
            var result = await probe.Detect(Ct);
            Assert.NotNull(result);
            Assert.Equal("qwen2.5-coder:14b", result.PreferredModel);
            Assert.True(result.HasNomicEmbed);
            await serveTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTOCHAT_DISABLE_LOCAL_OLLAMA_PROBE", prev);
            listener.Stop();
        }
    }

    private async Task<PublishedView> OpenInoAsync(IScreen screen)
    {
        await screen.Request("ino.open", JsonSerializer.SerializeToElement(new InoOpen()));
        return await fixture.WaitForScreenAsync(
            screen,
            v => InoSiloFixture.TryGetConversation(v, out _),
            Ct);
    }
}
