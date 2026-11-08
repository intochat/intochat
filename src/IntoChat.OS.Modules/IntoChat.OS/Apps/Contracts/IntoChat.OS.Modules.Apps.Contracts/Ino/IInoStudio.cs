using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.studio"), DefaultNeuronType("ino.studio")]
public interface IInoStudio
{
    // Starts the unit flow for the goal: the unit is authored, verified against its scenarios, deployed and waits for the person's
    // consent to its needs. The card points at the flow on the Tasks board; nothing is approved on the user's behalf.
    Task<InoToolResult> Make(string goal);

    Task<InoToolResult> Launch(string appId);

    // The studios and the deployed units, each with whether its spec.md and scenarios.json are kept.
    [DigitalBrain.ReadOnlyCall]
    Task<InoToolResult> List();
}
