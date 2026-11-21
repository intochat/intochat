using System.Net;
using System.Text;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class OpenAiCompatRuntimeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Script(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Refuse : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Connection refused");
        }
    }

    private sealed class Hang : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(Ct))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task ListsWhatTheServerServes()
    {
        var script = new Script("""{"data":[{"id":"mistral-7b-instruct","owned_by":"lmstudio"},{"id":"deepseek-r1-distill","owned_by":"lmstudio"}]}""");
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(script, disposeHandler: false));

        Assert.Equal(RuntimeId.OpenAiCompat("lmstudio"), runtime.Id);
        Assert.Equal("openai-compat:lmstudio", runtime.Id.Value);

        var status = await runtime.Probe(Ct);
        Assert.True(status.Reachable);
        Assert.Equal("http://localhost:1234/v1/models", script.LastUri?.ToString());

        var cards = await Collect(runtime.Installed(Ct));
        Assert.Equal(2, cards.Count);
        Assert.All(cards, static c =>
        {
            Assert.Equal(AiProvider.OpenAICompatible, c.Provider);
            Assert.Equal(Where.Device, c.Where);
            Assert.Equal("lmstudio", c.Family);
            Assert.Equal(Abilities.Text | Abilities.Tools, c.Can);
            Assert.Equal(Tier.Balanced, c.Tier);
        });
        Assert.Equal(["mistral-7b-instruct", "deepseek-r1-distill"], cards.Select(static c => c.Id.Value));

        var catalog = await Collect(runtime.Catalog(Ct));
        Assert.Equal(cards.Select(static c => c.Id), catalog.Select(static c => c.Id));
    }

    [Fact]
    public async Task InstallSaysThereIsNothingToDownload()
    {
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(new Script("{}"), disposeHandler: false));
        var progress = Assert.Single(await Collect(runtime.Install(new ModelId("any-model"), Ct)));
        Assert.Equal(InstallState.Failed, progress.State);
        Assert.Equal("lmstudio has no downloads; load the model in lmstudio.", progress.Error);

        var removeError = await Assert.ThrowsAsync<NotSupportedException>(() => runtime.Remove(new ModelId("any-model"), Ct));
        Assert.Equal("lmstudio has no downloads; load the model in lmstudio.", removeError.Message);
    }

    [Fact]
    public async Task UnreachableServerProbesFalse()
    {
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(new Refuse(), disposeHandler: false));
        var status = await runtime.Probe(Ct);
        Assert.False(status.Reachable);
        Assert.Empty(await Collect(runtime.Installed(Ct)));
    }

    [Fact]
    public async Task ProbeAndInstalledShareCacheWithinTtlAndRefreshAfterExpiry()
    {
        var clock = new FakeTimeProvider();
        var script = new Script("""{"data":[{"id":"mistral-7b-instruct","owned_by":"lmstudio"}]}""");
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(script, disposeHandler: false), clock);

        var firstProbe = await runtime.Probe(Ct);
        var installed = await Collect(runtime.Installed(Ct));
        var catalog = await Collect(runtime.Catalog(Ct));
        var secondProbe = await runtime.Probe(Ct);

        Assert.True(firstProbe.Reachable);
        Assert.True(secondProbe.Reachable);
        Assert.Single(installed);
        Assert.Single(catalog);
        Assert.Equal(1, script.Calls);

        clock.Advance(OpenAiCompatRuntime.CacheTtl + TimeSpan.FromSeconds(1));
        var refreshed = await runtime.Probe(Ct);
        Assert.True(refreshed.Reachable);
        Assert.Equal(2, script.Calls);
    }

    [Fact]
    public async Task UnreachableProbeCachesWithinTtlAndInvalidatesOnInstallAndRemove()
    {
        var clock = new FakeTimeProvider();
        var refuse = new Refuse();
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(refuse, disposeHandler: false), clock);

        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Empty(await Collect(runtime.Installed(Ct)));
        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Equal(1, refuse.Calls);

        _ = await Collect(runtime.Install(new ModelId("mistral-7b"), Ct));
        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Equal(2, refuse.Calls);

        await Assert.ThrowsAsync<NotSupportedException>(() => runtime.Remove(new ModelId("mistral-7b"), Ct));
        Assert.False((await runtime.Probe(Ct)).Reachable);
        Assert.Equal(3, refuse.Calls);
    }

    [Fact]
    public async Task StalledProbeTimesOutWithinTightTimeout()
    {
        Assert.True(OpenAiCompatRuntime.RequestTimeout <= TimeSpan.FromMilliseconds(1500));

        var clock = new FakeTimeProvider();
        var hang = new Hang();
        var runtime = new OpenAiCompatRuntime("lmstudio", new Uri("http://localhost:1234/v1"), new HttpClient(hang, disposeHandler: false), clock);

        var probeTask = runtime.Probe(Ct);
        await hang.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        clock.Advance(OpenAiCompatRuntime.RequestTimeout + TimeSpan.FromMilliseconds(50));

        var status = await probeTask.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(status.Reachable);
    }
}
