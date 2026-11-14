using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Ino;

public sealed class InoErrorsFacts
{
    [Theory]
    [InlineData("no-provider", "Ino has no model to answer with.", "Set up a model", false)]
    [InlineData("provider-rejected-key", "The model provider refused the key.", "Open API keys", false)]
    [InlineData("provider-unavailable", "The model provider did not answer.", "Try again", true)]
    [InlineData("timed-out", "The model provider did not answer.", "Try again", true)]
    [InlineData("budget", "The monthly limit is reached.", "Open billing", false)]
    [InlineData("too-long", "That message is too long.", null, false)]
    [InlineData("interrupted", "Ino was interrupted.", "Try again", true)]
    [InlineData("tool-failed", "Ino could not answer.", "Try again", true)]
    [InlineData("unknown", "Ino could not answer.", "Try again", true)]
    [InlineData("something-new", "Ino could not answer.", "Try again", true)]
    public void ACauseHasItsLine(string code, string what, string? action, bool retry)
    {
        var line = InoErrors.Of(new InoError(code));
        Assert.Equal((what, action, retry), (line.What, line.ActionLabel, line.Retry));
    }

    [Fact]
    public void APauseShowsItsReason()
    {
        var line = InoErrors.Of(new InoError("paused", "An interrupt holds the agent."));
        Assert.Equal("Ino is paused.", line.What);
        Assert.Equal("An interrupt holds the agent.", line.Detail);
        Assert.Null(line.ActionLabel);
    }
}
