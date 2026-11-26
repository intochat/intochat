using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.Social;

public sealed record SharedSourceFile(string Path, string Content);

/// <summary>A shareable app definition carrying only source files, title, and id (never author state, bindings, or secrets).</summary>
public sealed record AppDefinitionShare(string AppId, string Title, SharedSourceFile[] Files);

// A post as its author hands it in. AudienceAccounts is the account list of the first version of this record: with no
// Audience it is the audience, frozen; with one, the rule decides who sees the post and the list only names accounts to
// offer the store to at once. Id publishes under a chosen id (an id the store holds is replaced); every member after
// SharedApp is optional and absent in a draft written before it existed.
public sealed record PostDraft(
    string Text,
    string[] AudienceAccounts,
    AppDefinitionShare? SharedApp = null,
    string? Id = null,
    string? Title = null,
    Audience? Audience = null,
    PostEmbeddedApp[]? EmbeddedApps = null,
    string? ForkedFrom = null,
    DateTimeOffset? CreatedAt = null,
    string? TemplateSource = null);

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
    DateTimeOffset CreatedAt,
    AppDefinitionShare? SharedApp = null,
    // Added after the first version; a post stored or sent without them reads as null. A null Audience means the frozen
    // AudienceAccounts list.
    string? Title = null,
    Audience? Audience = null,
    PostEmbeddedApp[]? EmbeddedApps = null,
    string? ForkedFrom = null,
    string? TemplateSource = null);

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
