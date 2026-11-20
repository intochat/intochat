using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Rating.Signals;

[Signal("ui.rating-changed")]
public sealed record RatingChanged(string RatingId, int Version, int Value) : Signal;
