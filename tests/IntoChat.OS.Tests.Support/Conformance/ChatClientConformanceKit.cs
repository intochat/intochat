using IntoChat.OS.Tests.Support.Fakes.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace IntoChat.OS.Tests.Support.Conformance;

/// <summary>
/// Conformance kit for the AI <see cref="IChatClient"/> outside edge: verifies text turns,
/// tool-call turns with <see cref="FunctionResultContent"/> recording, and exhausted script failure messages.
/// </summary>
public abstract class ChatClientConformanceKit
{
    /// <summary>Creates a chat client driven by <paramref name="script"/>.</summary>
    protected abstract IChatClient CreateClient(LlmScript script);

    /// <summary>A scripted text turn returns the scripted assistant text and consumes the turn.</summary>
    [Fact]
    public async Task TextTurnReturnsScriptedText()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.Says("Hello from the scripted model."));
        using var client = CreateClient(script);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hi")], cancellationToken: ct).ConfigureAwait(false);

        Assert.Equal("Hello from the scripted model.", response.Text);
        script.ShouldBeFinished();
    }

    /// <summary>A tool-call turn proposes a <see cref="FunctionCallContent"/> and records the returned <see cref="FunctionResultContent"/> on the next turn.</summary>
    [Fact]
    public async Task ToolCallTurnProposesCallAndRecordsFunctionResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.CallsTool("lookup_company", new { company = "Acme" }))
            .Turn(t => t.Says("Saved Acme."));
        using var client = CreateClient(script);

        var userMessage = new ChatMessage(ChatRole.User, "Look up Acme");
        var first = await client.GetResponseAsync([userMessage], cancellationToken: ct).ConfigureAwait(false);

        var assistantMessage = Assert.Single(first.Messages);
        var call = Assert.Single(assistantMessage.Contents.OfType<FunctionCallContent>());
        Assert.Equal("lookup_company", call.Name);
        Assert.False(string.IsNullOrWhiteSpace(call.CallId));
        Assert.NotNull(call.Arguments);
        Assert.Equal("Acme", call.Arguments["company"]?.ToString());

        var toolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "{\"found\":true}")]);
        var second = await client.GetResponseAsync([userMessage, assistantMessage, toolResult], cancellationToken: ct).ConfigureAwait(false);

        Assert.Equal("Saved Acme.", second.Text);
        var seen = Assert.Single(script.Calls);
        Assert.Equal("lookup_company", seen.Name);
        Assert.Contains("Acme", seen.ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal("{\"found\":true}", seen.ResultText);
        script.ShouldBeFinished();
    }

    /// <summary>Calling the client after all scripted turns are consumed throws an actionable error naming the user prompt.</summary>
    [Fact]
    public async Task ExhaustedScriptFailsWithActionableMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var script = LlmScript.New()
            .Turn(t => t.Says("First and only turn."));
        using var client = CreateClient(script);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Turn 1")], cancellationToken: ct).ConfigureAwait(false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "Unexpected follow-up")], cancellationToken: ct)).ConfigureAwait(false);

        Assert.Contains("no turn left", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unexpected follow-up", error.Message, StringComparison.Ordinal);
    }
}
