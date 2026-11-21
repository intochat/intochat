using System.Net;
using System.Net.Sockets;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;

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

}
