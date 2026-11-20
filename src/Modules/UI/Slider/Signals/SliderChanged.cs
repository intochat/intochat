using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Slider.Signals;

[Signal("ui.slider-changed")]
public sealed record SliderChanged(string SliderId, int Version, double Value) : Signal;
