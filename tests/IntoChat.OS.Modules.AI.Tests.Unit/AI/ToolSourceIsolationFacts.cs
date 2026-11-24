using System.Text.Json;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Tools;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class ToolSourceIsolationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForKeysQueriesOnlySpecifiedSourcesAndDeduplicatesKeys()
    {
        var queriedKeys = new List<string>();
        var tools = await ToolSourceTools.ForKeys(
            call: (_, key, method, _, _) =>
            {
                Assert.Equal("Tools", method);
                queriedKeys.Add(key);
                var set = key.EndsWith("/treg", StringComparison.Ordinal)
                    ? new ToolSet("Treg",
                    [
                        new ToolDescriptor("list_issues", "List Issues", "List treg issues.", """{"type":"object"}""", ReadOnly: true),
                        new ToolDescriptor("get_issue", "Get Issue", "Get treg issue.", """{"type":"object"}""", ReadOnly: true),
                    ])
                    : new ToolSet("Secret",
                    [
                        new ToolDescriptor("exfiltrate", "Exfiltrate", "Should never be exposed.", """{"type":"object"}""", ReadOnly: false),
                    ]);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(set, JsonSerializerOptions.Web));
            },
            sources:
            [
                (typeof(IMcpToolConnection).FullName!, "owner/connection/mcp/treg"),
                (typeof(IMcpToolConnection).FullName!, "owner/connection/mcp/treg"),
            ],
            edge: null,
            conversation: "owner/conversations/agent-treg",
            ct: Ct);

        Assert.Equal(["owner/connection/mcp/treg"], queriedKeys);
        Assert.Equal(["treg_list_issues", "treg_get_issue"], tools.Select(t => t.Name).ToArray());
    }

    [Fact]
    public async Task ForKeysExposesUpToSixteenToolsDirectlyWithoutDroppingAny()
    {
        var descriptors = Enumerable.Range(1, 12)
            .Select(i => new ToolDescriptor($"tool_{i}", $"Tool {i}", $"Description {i}", """{"type":"object"}""", ReadOnly: true))
            .ToArray();

        var tools = await ToolSourceTools.ForKeys(
            call: (_, _, _, _, _) => Task.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement(new ToolSet("Treg", descriptors), JsonSerializerOptions.Web)),
            sources: [(typeof(IMcpToolConnection).FullName!, "owner/connection/mcp/treg")],
            edge: null,
            conversation: "owner/conversations/agent-treg",
            ct: Ct);

        Assert.Equal(12, tools.Count);
    }

    [Fact]
    public async Task ForKeysReturnsEmptyWhenGivenNoSources()
    {
        var called = false;
        var tools = await ToolSourceTools.ForKeys(
            call: (_, _, _, _, _) =>
            {
                called = true;
                return Task.FromResult<JsonElement?>(null);
            },
            sources: [],
            edge: null,
            conversation: "owner/conversations/agent-treg",
            ct: Ct);

        Assert.False(called);
        Assert.Empty(tools);
    }
}
