using DigitalBrain.Invocation;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.Shell.Conversations;

public sealed class ConversationContractsFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<ConversationEntry> Entries => new()
    {
        new Said(EntryIds.Said("r1"), new Participant.Human(new AccountId("a1")), At, "hi", new ModelId("qwen3.5:4b")),
        new Called(EntryIds.Call("r1", "c1"), new Participant.Assistant("ino"), At, "focus_show", """{"title":"t"}"""),
        new Returned(EntryIds.Result("r1", "c1"), new Participant.Assistant("ino"), At, "focus_show", "{}", true, new CardEntry("node", "Fruit", """{"kind":"text","value":"x"}""")),
        new Summarized(EntryIds.Summary("r2"), new Participant.Assistant("ino"), At, "earlier", EntryIds.Reply("r1")),
    };

    [Theory, MemberData(nameof(Entries))]
    public void EveryEntryRoundTripsThroughTheRuntimeProtocol(ConversationEntry entry) =>
        Assert.Equal(entry, (ConversationEntry)RuntimeProtocol.Deserialize(RuntimeProtocol.SerializeToUtf8Bytes(entry, typeof(ConversationEntry)), typeof(ConversationEntry))!);

    [Fact]
    public void VisibilityIsFixedByKind()
    {
        Assert.Equal(Visibility.Agents, new Called(EntryIds.Call("r", "c"), new Participant.Assistant("ino"), At, "t", "{}").Visible);
        Assert.Equal(Visibility.Everyone, new Said(EntryIds.Reply("r"), new Participant.Assistant("ino"), At, "x").Visible);
        Assert.Equal(Visibility.Everyone, new Returned(EntryIds.Result("r", "c"), new Participant.Assistant("ino"), At, "t", "{}", true, null).Visible);
        Assert.Equal(Visibility.Agents, new Summarized(EntryIds.Summary("r"), new Participant.Assistant("ino"), At, "sum", EntryIds.Reply("r0")).Visible);
    }

    [Fact]
    public void EntryIdsRoundTrip()
    {
        Assert.Equal("r1", EntryIds.RunOf(EntryIds.Result("r1", "c9")));
        Assert.Equal("r1", EntryIds.RunOf(EntryIds.Said("r1")));
        Assert.Equal("r1", EntryIds.RunOf(EntryIds.Reply("r1")));
        Assert.Equal("r1", EntryIds.RunOf(EntryIds.Summary("r1")));
        Assert.Equal("c9", EntryIds.CallOf(EntryIds.Call("r1", "c9")));
        Assert.Equal("c9", EntryIds.CallOf(EntryIds.Result("r1", "c9")));
        Assert.Null(EntryIds.CallOf(EntryIds.Reply("r1")));
        Assert.Null(EntryIds.RunOf(EntryIds.Post("greeting")));
        Assert.Null(EntryIds.CallOf(EntryIds.Post("greeting")));
    }
}
