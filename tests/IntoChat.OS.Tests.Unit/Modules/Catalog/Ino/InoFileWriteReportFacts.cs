using DigitalBrain;
using DigitalBrain.Operations;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// A file write Ino asked for ends as a durable operation; its outcome is recorded once for the conversation that asked.
public sealed class InoFileWriteReportFacts
{
    private const string Conversation = "ino/conversations/c1";
    private static readonly InoFilesState Waiting = new([new InoFileWrite("op-1", Conversation, "hello.txt", "Documents")]);

    private static OperationDone Done(string operation, OperationResult result) => new(new OperationId(operation), result);

    [Fact]
    public void ASucceededWriteIsRecordedForTheAskingConversation()
    {
        var settled = InoFileWriteReports.Settle(Waiting, Done("op-1", new OperationResult.Succeeded()));

        Assert.NotNull(settled);
        Assert.Equal("Created hello.txt in Documents.", settled.Value.Outcome);
        var unposted = Assert.Single(InoFileWriteReports.Unposted(settled.Value.Next));
        Assert.Equal(Conversation, unposted.Conversation);
    }

    [Fact]
    public void AnOutcomeIsRecordedOnlyOnce()
    {
        var once = InoFileWriteReports.Settle(Waiting, Done("op-1", new OperationResult.Succeeded()))!.Value.Next;

        Assert.Null(InoFileWriteReports.Settle(once, Done("op-1", new OperationResult.Succeeded())));
    }

    [Fact]
    public void ADeclinedWriteSaysNothingWasCreated()
    {
        var settled = InoFileWriteReports.Settle(Waiting, Done("op-1", new OperationResult.Failed("declined", "The confirmation was denied.")));

        Assert.StartsWith("Did not create hello.txt", settled!.Value.Outcome);
    }

    [Fact]
    public void AnotherOperationIsIgnored()
        => Assert.Null(InoFileWriteReports.Settle(Waiting, Done("op-other", new OperationResult.Succeeded())));

    [Fact]
    public void AWaitingWriteIsNotPostedYet() => Assert.Empty(InoFileWriteReports.Unposted(Waiting));
}
