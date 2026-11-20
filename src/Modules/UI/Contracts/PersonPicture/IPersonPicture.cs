using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.UI.PersonPicture;

[GenerateSerializer, Alias("ui.person-picture.state")]
public sealed record PersonPictureState(
    [property: Id(0)] int Version,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string Initials,
    [property: Id(3)] string? AvatarUrl,
    [property: Id(4)] string Presence,
    [property: Id(5)] string? Subtitle)
{
    public PersonPictureState() : this(0, "Owner", "OW", null, "online", null) { }
}

[NeuronAlias("ui.person-picture")]
[DefaultNeuronType("ui.person-picture")]
public interface IPersonPicture
{
    Task<PersonPictureState> Configure(string displayName, string? subtitle = null, string? avatarUrl = null, string presence = "online");
    Task<PersonPictureState> SetPresence(string presence);
    [ReadOnlyCall] Task<PersonPictureState> Read();
}
