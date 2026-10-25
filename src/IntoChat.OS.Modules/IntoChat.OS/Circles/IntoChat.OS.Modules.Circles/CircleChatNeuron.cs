using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Platform.Identity;
using IntoChat.OS.Sdk.Platform.Identity;
using IntoChat.OS.Connections.Identity;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Circles;

[GenerateSerializer, Alias("circles.chat-record")]
[ExportableState]
internal sealed class CircleChatRecord
{
    [Id(0)] public CircleLine[] Lines { get; set; } = [];
    [Id(1)] public long Last { get; set; }
    [Id(2)] public string[] Said { get; set; } = [];
    [Id(3)] public string[] Published { get; set; } = [];
}

// A circle brain's chat `{circle}/chat` (T26-1): one line per Say, numbered here, its speaker the member account that
// said it. A line said twice under one request id is one line.
[GrainType("circles.chat")]
internal sealed class CircleChatNeuron(
    [PersistentState("circles.chat", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CircleChatRecord> store,
    IIdentityClient identity)
    : Neuron<CircleChatRecord>(store), ICircleChat, ICirclePostReader
{
    private const int Kept = 500;
    private const int KeptRequests = 64;

    private string CircleBrain => this.GetPrimaryKeyString()[..^"/chat".Length];

    private ICircle Circle => GrainFactory.GetGrain<ICircle>(ICircle.Key(CircleBrain));

    public async Task Say(string text, string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var (speaker, circle) = await Member();
        await Append(speaker, circle, text, requestId);
    }

    private async Task Append(string speaker, CircleState circle, string text, string? requestId, bool post = false)
    {
        var said = string.IsNullOrWhiteSpace(requestId) ? null : $"{speaker}/{requestId}";
        if (said is not null && (post ? Snapshot.Published : Snapshot.Said).Contains(said, StringComparer.Ordinal)) { return; }
        var line = new CircleLine(speaker, text, Snapshot.Last + 1);
        var lines = Snapshot.Lines.Length >= Kept ? Snapshot.Lines[1..] : Snapshot.Lines;
        await Save(new CircleChatRecord { Lines = [.. lines, line], Last = line.Sequence, Published = post && said is not null ? [.. Snapshot.Published, said] : Snapshot.Published, Said = post || said is null ? Snapshot.Said : [.. Snapshot.Said.TakeLast(KeptRequests - 1), said] },
            new CircleSpoke(circle.Name, line.Speaker, line.Text, line.Sequence));
    }

    public Task<CircleLine[]> PostReferences() => Task.FromResult(Snapshot.Lines.Where(line => line.Text.StartsWith("post ", StringComparison.Ordinal)).ToArray());

    public async Task PublishPost(string postKey, string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var caller = CallerContextStamper.Require();
        var brain = BrainScope.Create(caller.AccountId, caller.BrainId).Id;
        var prefix = brain + "/posts/";
        if (caller.Kind != CallerKind.App || caller.AppId != IApp.Key(brain, "friends")
            || !postKey.StartsWith(prefix, StringComparison.Ordinal)
            || postKey.Length == prefix.Length
            || !postKey[prefix.Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        { throw new UnauthorizedAccessException("Only the personal Friends app publishes its own post references."); }
        var owner = await identity.Membership(brain, caller.PersonId);
        if (owner?.AccountId != caller.AccountId || owner.Role != MemberRole.Owner
            || await identity.KindOf(brain) != BrainKind.Personal)
        { throw new UnauthorizedAccessException("The author must own the personal brain."); }
        var membership = await identity.Membership(CircleBrain, caller.PersonId);
        var circle = await GrainFactory.GetGrain<ICircleStateReader>(ICircle.Key(CircleBrain)).State();
        if (membership?.AccountId != caller.AccountId || !circle.Members.Contains(caller.AccountId, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException("The author must currently belong to the circle."); }
        await Append(caller.AccountId, circle, "post " + postKey, requestId, post: true);
    }

    public async Task<CircleLine[]> Read(long since)
    {
        await Member();
        return [.. Snapshot.Lines.Where(line => line.Sequence > since)];
    }

    // A member of the circle brain, under its own account (T26-8).
    private async Task<(string Account, CircleState Circle)> Member()
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.BrainId != CircleBrain)
        { throw new UnauthorizedAccessException($"The caller is not in circle {CircleBrain}."); }
        var circle = await Circle.State();
        if (!circle.Members.Contains(caller.AccountId)) { throw new UnauthorizedAccessException($"The caller is not a member of circle {CircleBrain}."); }
        return (caller.AccountId, circle);
    }
}
