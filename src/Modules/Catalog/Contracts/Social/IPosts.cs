using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

public sealed record PostDraft(string Text, string[] AudienceAccounts);

public sealed record PostReaction(string Account, string Kind, DateTimeOffset At);

public sealed record PostComment(string Id, string Author, string Text, DateTimeOffset At);

public sealed record SocialPost(
    string Id,
    string Author,
    string Text,
    string[] AudienceAccounts,
    PostReaction[] Reactions,
    PostComment[] Comments,
    long Sequence,
    DateTimeOffset CreatedAt);

public sealed record PostPage(SocialPost[] Posts, long NextCursor);

[Shareable, Offered, NeuronAlias("social.posts"), DefaultNeuronType("social.posts")]
public interface IPosts
{
    const string DefaultKey = "default";

    Task<string> Publish(PostDraft draft, CancellationToken ct = default);

    [Shared, ReadOnlyCall]
    Task<PostPage> Since(long cursor = 0, CancellationToken ct = default);

    [Shared]
    Task React(string postId, string reaction, CancellationToken ct = default);

    [Shared]
    Task<string> Comment(string postId, string text, CancellationToken ct = default);
}
