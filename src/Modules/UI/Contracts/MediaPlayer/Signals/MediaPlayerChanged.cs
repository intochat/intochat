using DigitalBrain;

namespace IntoChat.OS.Modules.UI.MediaPlayer.Signals;

[Signal("ui.media-player-changed")]
public sealed record MediaPlayerChanged(string PlayerId, int Version, bool Playing, double PositionSeconds) : Signal;
