using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("ino.studio"), DefaultNeuronType("ino.studio")]
public interface IInoStudio
{
    // Starts a task for the goal and returns a pending approval card; nothing is approved on the user's behalf.
    Task<InoToolResult> Make(string goal);

    Task<InoToolResult> Launch(string appId);

    [DigitalBrain.ReadOnlyCall]
    Task<InoToolResult> List();
}
