using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Operations;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.Flutter.Conversations;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed record OpPumpState(int Sent)
{
    public OpPumpState() : this(0) { }
}

public interface IOpDonePump
{
    Task Emit(string operation, OperationResult result);
}

[DigitalBrain.Neurons.Neuron(Alias)]
public sealed class OpDonePump : DigitalBrain.Neurons.Neuron<OpPumpState>, IOpDonePump
{
    public const string Alias = "test.op-done-pump";
    public Task Emit(string operation, OperationResult result)
        => Save(State with { Sent = State.Sent + 1 }, new OperationDone(new OperationId(operation), result));
}

// A file write Ino asked for ends as a durable operation; its outcome is recorded once for the conversation that asked.
public sealed class InoFileWriteReportFacts
{
    private sealed class NoSecrets : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

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

    [Theory]
    [InlineData("owner/ino/conversations/main", "ino/conversations/main")]
    [InlineData("ino/conversations/main", "ino/conversations/main")]
    [InlineData("other/ino/conversations/main", "other/ino/conversations/main")]
    public void TheAgentKeyDropsOnlyThisBrain(string conversation, string expected)
        => Assert.Equal(expected, InoFileWriteReports.AgentKey("owner", conversation));

    [Theory]
    [InlineData("owner/conversation/conversations/main", "conversations/main")]
    [InlineData("owner/conversations/main", "conversations/main")]
    [InlineData("conversations/main", "conversations/main")]
    [InlineData("owner/ino/conversations/main", "conversations/main")]
    [InlineData("ino/conversations/c-abc12345", "conversations/c-abc12345")]
    public void ConversationKeyNormalizesAllShapes(string raw, string expected)
        => Assert.Equal(expected, InoFileWriteReports.ConversationKey("owner", raw));

    [Fact]
    public async Task FileReportsArePostedToTheConversation()
    {
        var brain = new BrainId("owner");
        var services = new ServiceCollection()
            .AddSingleton<ISecretRedactor, NoSecrets>()
            .AddSingleton(TimeProvider.System)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = services);
        os.Register<InoFilesNeuron>();
        os.Register<OpDonePump>();
        os.Register(typeof(AIModule).Assembly);

        var filesId = new NeuronId(brain, "ino.files", IFiles.DefaultKey);
        const string stateJson = """{"writes":[{"operation":"op-42","conversation":"owner/conversation/conversations/main","name":"notes.txt","folder":"Documents","outcome":null}]}""";
        os.SeedStoredState(filesId, stateJson);

        using var person = os.Edge.Enter(os.Edge.Person("owner", "owner", brain));
        await os.Activate(filesId);
        await os.Get<IOpDonePump>(new NeuronId(brain, OpDonePump.Alias, "pump")).Emit("op-42", new OperationResult.Succeeded());
        await os.Idle();

        var conversation = os.Get<IConversation>(new NeuronId(brain, IConversation.Type, IConversation.KeyOf(ConversationId.Parse("main"))));
        var view = await conversation.Read();
        var posted = Assert.Single(view.Entries.OfType<Said>());
        Assert.Equal(EntryIds.Post("file/op-42"), posted.Id);
        Assert.Contains("Created notes.txt in Documents.", posted.Text, StringComparison.Ordinal);
    }
}
