using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Signals;

// A book a person of another brain may sign: Shared on a Shareable contract that is not Offered.
[Shareable, DefaultNeuronType("facts.guestbook")]
public interface IGuestbook
{
    [Shared] Task Sign(string text);
}

public sealed record GuestbookState(string[] Entries)
{
    public GuestbookState() : this([]) { }
}

[Signal("facts.guestbook.signed")]
public sealed record GuestbookSigned(string Text) : Signal;

[Neuron("facts.guestbook")]
public sealed class GuestbookNeuron : Neuron<GuestbookState>, IGuestbook
{
    public Task Sign(string text) => Save(new GuestbookState([.. State.Entries, text]), new GuestbookSigned(text));
}

[DefaultNeuronType("facts.guestbook.reader")]
public interface IGuestbookReader
{
    Task Follow(NeuronId book);
    [ReadOnlyCall] Task<string[]> Heard();
}

// What was heard, and who the reaction ran as: "{actor}@{brain}".
public sealed record GuestbookReaderState(string[] Heard)
{
    public GuestbookReaderState() : this([]) { }
}

[Neuron("facts.guestbook.reader")]
public sealed class GuestbookReaderNeuron : Neuron<GuestbookReaderState>, IGuestbookReader
{
    public Task Follow(NeuronId book) => Context.Wires.Ensure<GuestbookSigned>(book);

    [ReadOnlyCall]
    public Task<string[]> Heard() => Task.FromResult(State.Heard);

    [On]
    public Task On(GuestbookSigned entry)
        => Store(new GuestbookReaderState([.. State.Heard, $"{entry.Text} as {Context.Caller?.Actor.GetType().Name}@{Context.Caller?.Brain.Value}"]));
}

[Module("facts-foreign-caller-signals")]
public sealed class ForeignCallerSignalsModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

// A brain's own signals reach its own subscribers whoever's call made the neuron publish. A person of another brain
// signs the book; the book's brain delivers the signal to its reader as its own scheduler, never as the visitor.
public sealed class ForeignCallerSignalFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ASignalPublishedWhileServingAnotherBrainsCaller_WakesTheBrainsOwnSubscriber_AsTheBrainsScheduler(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(kind, [typeof(ForeignCallerSignalsModule)], ct: Ct);
        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var book = new NeuronId(ada.Brain, "facts.guestbook", "hall");
        var reader = ada.Get<IGuestbookReader>(new NeuronId(ada.Brain, "facts.guestbook.reader", "hall"));
        await reader.Follow(book);

        await bob.Get<IGuestbook>(book).Sign("bob was here");

        Assert.Equal([$"bob was here as {nameof(Actor.Scheduler)}@{ada.Brain.Value}"], await HeardWithin(reader, 1));
    }

    // The brain's own caller is not replaced: its reactions keep running as the caller that made the neuron publish.
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ASignalPublishedForTheBrainsOwnCaller_IsDeliveredAsThatCaller(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(kind, [typeof(ForeignCallerSignalsModule)], ct: Ct);
        var ada = os.Person("ada");
        var book = new NeuronId(ada.Brain, "facts.guestbook", "hall");
        var reader = ada.Get<IGuestbookReader>(new NeuronId(ada.Brain, "facts.guestbook.reader", "hall"));
        await reader.Follow(book);

        await ada.Get<IGuestbook>(book).Sign("ada was here");

        Assert.Equal([$"ada was here as {nameof(Actor.Person)}@{ada.Brain.Value}"], await HeardWithin(reader, 1));
    }

    private static async Task<string[]> HeardWithin(IGuestbookReader reader, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var heard = await reader.Heard();
            if (heard.Length >= count || DateTimeOffset.UtcNow > deadline) { return heard; }
            await Task.Delay(50, Ct);
        }
    }
}
