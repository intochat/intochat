using System.Diagnostics;
using IntoChat.OS.Modules.AI.Agents;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

// A tool call is its own execute_tool span naming the neuron it reached; what the model sent and got back is recorded only when
// sensitive telemetry is on.
public sealed class AgentTelemetryFacts
{
    private static FunctionCallContent Call() => new("c1", "neurons_call", new Dictionary<string, object?>
    {
        ["contract"] = "IntoChat.OS.Modules.Apps.IFiles",
        ["key"] = "{brain}/ino/files",
        ["method"] = "Write",
        ["arguments"] = "[\"a.txt\",\"secret\"]",
    });

    private static List<Activity> Listen(out ActivityListener listener)
    {
        var stopped = new List<Activity>();
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return stopped;
    }

    [Fact]
    public void AToolSpanNamesTheNeuronItReached()
    {
        var stopped = Listen(out var listener);
        using (listener)
        {
            using var span = AgentTelemetry.Tool(Call(), sensitive: false);
            AgentTelemetry.Result(span, "{\"ok\":true}", sensitive: false);
        }
        var tool = Assert.Single(stopped);
        Assert.Equal("execute_tool neurons_call", tool.DisplayName);
        Assert.Equal("IntoChat.OS.Modules.Apps.IFiles", tool.GetTagItem("intochat.contract"));
        Assert.Equal("Write", tool.GetTagItem("intochat.method"));
        Assert.Null(tool.GetTagItem("gen_ai.tool.call.arguments"));
        Assert.Null(tool.GetTagItem("gen_ai.tool.call.result"));
        Assert.Equal(ActivityStatusCode.Ok, tool.Status);
    }

    [Fact]
    public void SensitiveTelemetryRecordsArgumentsAndResult()
    {
        var stopped = Listen(out var listener);
        using (listener)
        {
            using var span = AgentTelemetry.Tool(Call(), sensitive: true);
            AgentTelemetry.Result(span, "{\"isError\":true}", sensitive: true);
        }
        var tool = Assert.Single(stopped);
        Assert.Contains("secret", (string)tool.GetTagItem("gen_ai.tool.call.arguments")!, StringComparison.Ordinal);
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
    }

    [Theory]
    [InlineData("owner/ino/conversations/main", "ino")]
    [InlineData("solo", "solo")]
    public void TheAgentIsNamedByItsApp(string conversation, string expected) => Assert.Equal(expected, AgentTurnLoop.AgentName(conversation));

    [Fact]
    public void FirstTokenIsAnEventOnTheTurn()
    {
        var stopped = Listen(out var listener);
        using (listener)
        {
            using var turn = AgentTelemetry.Turn("ino", "conversations/c-1", "r1");
            AgentTelemetry.FirstToken(turn, TimeSpan.FromMilliseconds(420));
            Assert.Equal(
                420d,
                Assert.Single(turn!.Events, e => e.Name == "gen_ai.first_token")
                    .Tags.Single(t => t.Key == "intochat.first_token_ms").Value);
        }
        Assert.Single(stopped);
    }
}
