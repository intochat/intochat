using IntoChat.OS.AI.Agents.Intents;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoIntentFacts
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PendingRoundTrips()
    {
        var pending = new Pending("hello", "\n\nhint", "brain/apps/ino/conversations/main", Now, false);
        Assert.Equal(pending, Pending.Parse(pending.Serialize()));
        Assert.Null(Pending.Parse(""));
        Assert.Null(Pending.Parse("not json"));
    }

    [Fact]
    public void OlderFindsOnlyStaleUnstartedEntries()
    {
        var stale = Guid.NewGuid();
        var started = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var entries = new Dictionary<Guid, Pending>
        {
            [stale] = new("a", "", "c", Now.AddSeconds(-9), false),
            [started] = new("b", "", "c", Now.AddSeconds(-9), true),
            [fresh] = new("c", "", "c", Now.AddSeconds(-2), false),
        };
        Assert.Equal([stale], PendingStore.Older(entries, Now, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public void AConfirmedBriefRendersAnActInstruction()
    {
        var text = Ino.BriefInstructions(new IntentBrief("Add two numbers", ["17", "25"], ["use the calculator"], ["the sum is shown"]), true);
        Assert.Contains("Act on this brief:", text);
        Assert.Contains("Add two numbers", text);
        Assert.Contains("17", text);
        Assert.Contains("use the calculator", text);
        Assert.Contains("the sum is shown", text);
        Assert.DoesNotContain("do not act on it yet", text);
    }

    [Fact]
    public void AnUnconfirmedBriefRendersAnAskInstruction()
    {
        var text = Ino.BriefInstructions(new IntentBrief("Add two numbers", [], [], []), false);
        Assert.Contains("Show this brief to the user, ask whether it is right, and do not act on it yet", text);
        Assert.Contains("Add two numbers", text);
        Assert.DoesNotContain("Act on this brief:", text);
    }
}
