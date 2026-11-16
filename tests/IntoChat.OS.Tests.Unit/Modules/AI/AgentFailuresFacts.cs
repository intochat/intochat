using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class AgentFailuresFacts
{
    public static TheoryData<Exception, AgentFailure> Cases => new()
    {
        { new NoProviderConfiguredException("x"), AgentFailure.NoProvider },
        { new ProviderUnavailableException("anthropic", "Unconfigured", ["ApiKey"]), AgentFailure.NoProvider },
        { new HttpRequestException("x", null, HttpStatusCode.Unauthorized), AgentFailure.ProviderRejectedKey },
        { new HttpRequestException("x", null, HttpStatusCode.Forbidden), AgentFailure.ProviderRejectedKey },
        { new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable), AgentFailure.ProviderUnavailable },
        { new HttpRequestException("x"), AgentFailure.ProviderUnavailable },
        { new SocketException(), AgentFailure.ProviderUnavailable },
        { new TimeoutException(), AgentFailure.TimedOut },
        { new InvalidOperationException("x"), AgentFailure.Unknown },
        { new AggregateException(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)), AgentFailure.ProviderRejectedKey },
        { ToolFailedException(), AgentFailure.ToolFailed },
    };

    private static Exception ToolFailedException()
    {
        var ex = new InvalidOperationException("boom");
        ex.Data["AgentToolFailed"] = true;
        return ex;
    }

    [Theory, MemberData(nameof(Cases))]
    public void AnExceptionHasACause(Exception error, AgentFailure expected)
        => Assert.Equal(expected, AgentFailures.Classify(error));

    [Fact]
    public void CodeOfAndParseCodeRoundTripEveryDefinedFailure()
    {
        Assert.Equal("unknown", AgentFailures.CodeOf(AgentFailure.Unknown));
        Assert.Equal("no-provider", AgentFailures.CodeOf(AgentFailure.NoProvider));
        Assert.Equal("provider-rejected-key", AgentFailures.CodeOf(AgentFailure.ProviderRejectedKey));
        Assert.Equal("provider-unavailable", AgentFailures.CodeOf(AgentFailure.ProviderUnavailable));
        Assert.Equal("tool-failed", AgentFailures.CodeOf(AgentFailure.ToolFailed));
        Assert.Equal("timed-out", AgentFailures.CodeOf(AgentFailure.TimedOut));

        Assert.Equal(AgentFailure.NoProvider, AgentFailures.ParseCode("no-provider"));
        Assert.Equal(AgentFailure.ProviderRejectedKey, AgentFailures.ParseCode("provider-rejected-key"));
        Assert.Equal(AgentFailure.ProviderUnavailable, AgentFailures.ParseCode("provider-unavailable"));
        Assert.Equal(AgentFailure.ToolFailed, AgentFailures.ParseCode("tool-failed"));
        Assert.Equal(AgentFailure.TimedOut, AgentFailures.ParseCode("timed-out"));
        Assert.Equal(AgentFailure.Unknown, AgentFailures.ParseCode("something-else"));
    }

    [Fact]
    public void LocalOllamaParseTagsPrefersChatModelThenCoderFallbackAndDetectsNomicEmbedText()
    {
        const string rtx5080Json = """
        {
          "models": [
            { "name": "nomic-embed-text:latest" },
            { "name": "qwen2.5-coder:14b" },
            { "name": "qwen3.5:4b" }
          ]
        }
        """;

        using var doc1 = JsonDocument.Parse(rtx5080Json);
        var parsed = HttpLocalOllamaProbe.ParseTags(HttpLocalOllamaProbe.DefaultEndpoint, doc1.RootElement);
        Assert.NotNull(parsed);
        Assert.True(parsed.HasNomicEmbed);
        Assert.Equal("qwen3.5:4b", parsed.PreferredModel);
        Assert.Equal("qwen2.5-coder:14b", HttpLocalOllamaProbe.SelectPreferredCoderModel(parsed.InstalledModels));
        Assert.Contains("qwen2.5-coder:14b", parsed.InstalledModels);
        Assert.Contains("qwen3.5:4b", parsed.InstalledModels);

        const string coderOnlyJson = """
        {
          "models": [
            { "name": "nomic-embed-text:latest" },
            { "name": "qwen2.5-coder:14b" }
          ]
        }
        """;

        using var doc2 = JsonDocument.Parse(coderOnlyJson);
        var fallback = HttpLocalOllamaProbe.ParseTags(HttpLocalOllamaProbe.DefaultEndpoint, doc2.RootElement);
        Assert.NotNull(fallback);
        Assert.Equal("qwen2.5-coder:14b", fallback.PreferredModel);
    }
}
