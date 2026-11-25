using System.Text.Json.Serialization;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// Read once by IFriends; never written after S8.
public sealed record ProfileFriend(
    string BrainId,
    string Handle,
    string DisplayName,
    string Scope = "friends",
    DateTimeOffset AddedAt = default,
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
    public Audience? Audience { get; init; }

    [JsonIgnore]
    public Audience Seen => Audience ?? Audience.FromLegacy(Circle);
}

// Posts is what the profile kept before posts moved to the post store (IPosts). The store copies it once and nothing
// writes it any more; it stays readable so a brain that goes back to an earlier version still finds those posts (OWED.md).
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

    // Retired: posts live in the post store (IPosts, social.posts). The three methods below store nothing and refuse,
    // saying so; they stay so a caller built against them gets that answer and not a missing method (OWED.md).
    Task<ProfileState> PublishPost(string id, string content, Audience? audience = null);

    Task<ProfileState> PublishAppPost(ProfilePostItem post);

    Task<ProfileState> ForkPost(string postId, string newPostId, string? remixTitle = null);
}
