/*
# BrainView

The brain as a living picture: every neuron a point on a slowly turning sphere, every subscription an edge,
every signal a pulse along it. Tapping a point selects it; the panel beside the picture shows the synapse's
last runs and the trace of each. The picture reads the brain's own activity stream; this app only composes
the scene and keeps the panel in step with the selection. Until the kernel activity projection lands, the
picture is built from behavior runs and says so in its title.

Inputs are plain text.
- open: composes the brain scene and the panel, and keeps both in its declared surface.

## Scenario: Opening the brain view shows the picture

Invoking "open" composes the "brain" scene with a brain_graph node beside its detail panel in the declared surface.

## Scenario: Tapping a neuron fills the panel

A SceneInput with kind "tap" on the brain_graph node selects the neuron its payload names, and the panel
card's title is that neuron's key.
*/
using DigitalBrain.Apps;

static class App
{
    public const string Name = "brainview";
    public const string Version = "1.0.1";
    public static readonly AppManifest Manifest = new(
        Title: "Brain",
        Description: "See your brain think: neurons, subscriptions and live signals, down to one synapse's runs.",
        Operations: [new("open", "Compose the brain scene and its detail panel.")],
        Settings: [], Slots: [],
        Icon: "icon.svg");
}
