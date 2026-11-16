using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record ProfileFriend(
    string BrainId,
    string Handle,
    string DisplayName,
    string Scope = "friends",
    DateTimeOffset AddedAt = default,
    // The friend's account, and whether both sides confirmed the link with a friend code. A friend added by typing an
    // id, or restored from an archive, is unconfirmed: it is listed, and nothing can be shared with it.
    string Account = "",
    bool Confirmed = false);

public sealed record PostEmbeddedApp(
    string Slot,
    string AppId,
    string Title,
    string TargetNeuronUri);

public sealed record ProfilePostItem(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    string Circle,
    string Title = "",
    string TemplateAppId = "friends.post-template",
    string TemplateSource = "",
    PostEmbeddedApp[]? EmbeddedApps = null,
    string? ForkedFrom = null,
    IReadOnlyDictionary<string, string>? UnitFiles = null,
    string AuthorBrainId = "")
{
    public string Scope => string.IsNullOrWhiteSpace(Circle) ? "public" : Circle;
}

public sealed record ProfileState(
    string Handle,
    string Bio,
    string[] PinnedApps,
    ProfilePostItem[] Posts,
    string DisplayName = "",
    string AvatarUrl = "",
    ProfileFriend[]? Friends = null,
    string[]? SharedApps = null)
{
    public ProfileState() : this("user", "", [], [], "User", "", [], []) { }
}

[DigitalBrain.Signal("os.profile.changed")]
public sealed record ProfileChanged(string Handle, int PostCount) : DigitalBrain.Signal;

[NeuronAlias("os.profile"), DefaultNeuronType("os.profile")]
public interface IProfile
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<ProfileState> Read();

    Task<ProfileState> SetBio(string handle, string bio, string[] pinnedApps);

    Task<ProfileState> UpdateProfile(string handle, string displayName, string bio, string[] pinnedApps, string avatarUrl = "");

    Task<ProfileState> AddFriend(string friendBrainId, string handle, string displayName, string scope = "friends");

    Task<ProfileState> RemoveFriend(string friendBrainId);

    // Written only by the brain's own friend links, when a friend code was redeemed or a friend was removed.
    [DigitalBrain.Access.Internal] Task<ProfileState> ConfirmFriend(string account, string friendBrainId, string handle, string displayName);

    [DigitalBrain.Access.Internal] Task<ProfileState> RemoveFriendAccount(string account);

    Task<ProfileState> PublishPost(string id, string content, string circle = "public");

    Task<ProfileState> PublishAppPost(ProfilePostItem post);

    Task<ProfileState> ForkPost(string postId, string newPostId, string? remixTitle = null);
}
