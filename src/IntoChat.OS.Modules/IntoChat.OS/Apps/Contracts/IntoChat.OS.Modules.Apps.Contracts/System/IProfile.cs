using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

public sealed record ProfilePostItem(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    string Circle);

public sealed record ProfileState(
    string Handle,
    string Bio,
    string[] PinnedApps,
    ProfilePostItem[] Posts)
{
    public ProfileState() : this("user", "", [], []) { }
}

[DigitalBrain.Signal("os.profile.changed")]
public sealed record ProfileChanged(string Handle, int PostCount) : DigitalBrain.Signal;

[NeuronAlias("os.profile"), DefaultNeuronType("os.profile")]
public interface IProfile
{
    [DigitalBrain.ReadOnlyCall]
    Task<ProfileState> Read();

    Task<ProfileState> SetBio(string handle, string bio, string[] pinnedApps);

    Task<ProfileState> PublishPost(string id, string content, string circle = "public");
}
