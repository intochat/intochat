using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Components;

[Shareable, Offered]
[NeuronAlias("facts.offered.note"), DefaultNeuronType("facts.offered.note")]
public interface IOfferedNote
{
    [Shared, ReadOnlyCall] Task<string> Read();
    [Shared] Task Write(string text);
}

public sealed record OfferedNoteState(string Text)
{
    public OfferedNoteState() : this(string.Empty) { }
}

[Signal("facts.offered.note-changed")]
public sealed record OfferedNoteChanged(string Text) : Signal;

[Neuron("facts.offered.note")]
public sealed class OfferedNoteNeuron : Neuron<OfferedNoteState>, IOfferedNote
{
    [ReadOnlyCall]
    public Task<string> Read() => Task.FromResult(State.Text);

    public async Task Write(string text)
    {
        await Save(new OfferedNoteState(text), new OfferedNoteChanged(text)).ConfigureAwait(true);
    }
}

[Module("facts-offers")]
public sealed class OffersFactsModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class OfferFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnOfferCannotBeMadeForAnotherBrainsResource(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(OffersFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var bobsNote = new NeuronId(bob.Brain, "facts.offered.note", "n1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ada.Offers.Offer(bobsNote, bob.Account, Role.Viewer, TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task WithAViewerOfferAFriendReadsAndCannotWrite(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(OffersFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var noteId = new NeuronId(ada.Brain, "facts.offered.note", "n1");

        await ada.Get<IOfferedNote>(noteId).Write("hello");
        await ada.Offers.Offer(noteId, bob.Account, Role.Viewer, TestContext.Current.CancellationToken);

        var value = await bob.Get<IOfferedNote>(noteId).Read();
        Assert.Equal("hello", value);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<IOfferedNote>(noteId).Write("tamper"));
        Assert.Contains("offer.read-only", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task WithNoOfferAFriendIsRefused(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(OffersFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var noteId = new NeuronId(ada.Brain, "facts.offered.note", "n1");

        await ada.Get<IOfferedNote>(noteId).Write("secret");

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<IOfferedNote>(noteId).Read());
        Assert.Contains("offer.none", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ARevokedOfferIsRefusedOnTheNextCall(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(OffersFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var noteId = new NeuronId(ada.Brain, "facts.offered.note", "n1");

        await ada.Get<IOfferedNote>(noteId).Write("v1");
        await ada.Offers.Offer(noteId, bob.Account, Role.Viewer, TestContext.Current.CancellationToken);
        Assert.Equal("v1", await bob.Get<IOfferedNote>(noteId).Read());

        await ada.Offers.Revoke(noteId, bob.Account, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            bob.Get<IOfferedNote>(noteId).Read());
        Assert.Contains("offer.none", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AnEditorOfferMayWrite(NeuronRuntimeKind kind)
    {
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(OffersFactsModule)],
            ct: TestContext.Current.CancellationToken);

        var ada = os.Person("ada");
        var bob = os.Person("bob");
        var noteId = new NeuronId(ada.Brain, "facts.offered.note", "n1");

        await ada.Get<IOfferedNote>(noteId).Write("v1");
        await ada.Offers.Offer(noteId, bob.Account, Role.Editor, TestContext.Current.CancellationToken);

        await bob.Get<IOfferedNote>(noteId).Write("v2");
        Assert.Equal("v2", await ada.Get<IOfferedNote>(noteId).Read());
    }
}
