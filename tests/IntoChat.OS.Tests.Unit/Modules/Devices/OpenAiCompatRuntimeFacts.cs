using System.Net;
using System.Text;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;

namespace IntoChat.OS.Tests.Unit.Modules.Devices;

public sealed class OpenAiCompatRuntimeFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Script(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Connection refused");
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
}
