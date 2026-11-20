using DigitalBrain;

namespace IntoChat.OS.Modules.UI.PersonPicture.Signals;

[Signal("ui.person-picture-changed")]
public sealed record PersonPictureChanged(string PictureId, int Version, string Presence) : Signal;
