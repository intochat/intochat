using DigitalBrain;
using DigitalBrain.UI;

namespace Units.HelloWorld;

[Signal("helloworld.toggled")]
public sealed record HelloToggled(bool Visible, int Clicks) : Signal;

[State("helloworld")]
public sealed record HelloWorldState(
    bool Visible = false,
    int Clicks = 0)
{
    public HelloWorldState() : this(false, 0) { }
}

public static class HelloWorldUnit
{
    [Synapse("helloworld.toggle", "helloworld")]
    public static Reaction<HelloWorldState> Toggle(SignalEnvelope envelope, HelloWorldState state)
    {
        var nextVisible = !state.Visible;
        var nextClicks = state.Clicks + 1;
        return Reaction<HelloWorldState>.Of(
            state with { Visible = nextVisible, Clicks = nextClicks },
            new HelloToggled(nextVisible, nextClicks));
    }

    [Synapse("helloworld")]
    public static View View(HelloWorldState state) =>
        new("Hello World", new Surface(
        [
            new Row(
            [
                new Badge(state.Visible ? "Greeting Visible" : "Ready", state.Visible ? "success" : "neutral"),
                new StatCardNode("clicks", "Clicks", state.Clicks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]),
            new Text(state.Visible ? "Hello, IntoChat OS!" : "Click the button below"),
            new Button(
                Key: "toggle-hello",
                Label: state.Visible ? "Hide Hello" : "Say Hello",
                Emit: new Emit("helloworld", "helloworld.toggle"),
                Tone: ButtonTone.Accent),
        ], Title: "Hello World", Subtitle: "Interactive Micro-App"));
}
