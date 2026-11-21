using DigitalBrain.Invocation;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.Shell.Conversations;

public sealed class ConversationContractsFacts
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<Entry> Entries => new()
    {
        new Said(EntryIds.Said("r1"), new Participant.Person(new AccountId("a1")), At, "hi", new ModelId("qwen3.5:4b")),
        new Called(EntryIds.Call("r1", "c1"), new Participant.Agent("ino"), At, "focus_show", """{"title":"t"}"""),
        new Returned(EntryIds.Result("r1", "c1"), new Participant.Agent("ino"), At, "focus_show", "{}", true, new CardEntry("node", "Fruit", """{"kind":"text","value":"x"}""")),
        new Summarized(EntryIds.Summary("r2"), new Participant.Agent("ino"), At, "earlier", EntryIds.Reply("r1")),
    };

    [Theory, MemberData(nameof(Entries))]
    public void EveryEntryRoundTripsThroughTheRuntimeProtocol(Entry entry) =>
        Assert.Equal(entry, (Entry)RuntimeProtocol.Deserialize(RuntimeProtocol.SerializeToUtf8Bytes(entry, typeof(Entry)), typeof(Entry))!);

    [Fact]
    public void VisibilityIsFixedByKind()
    {
        Assert.Equal(Visibility.Agents, new Called(EntryIds.Call("r", "c"), new Participant.Agent("ino"), At, "t", "{}").Visible);
        Assert.Equal(Visibility.Everyone, new Said(EntryIds.Reply("r"), new Participant.Agent("ino"), At, "x").Visible);
        Assert.Equal(Visibility.Everyone, new Returned(EntryIds.Result("r", "c"), new Participant.Agent("ino"), At, "t", "{}", true, null).Visible);
        Assert.Equal(Visibility.Agents, new Summarized(EntryIds.Summary("r"), new Participant.Agent("ino"), At, "sum", EntryIds.Reply("r0")).Visible);
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
