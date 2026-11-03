using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Flame.Rules;

internal sealed record SceneNodeExists(string NodeId, bool Exists) : Rule("flame.scene-node-exists")
{
    public override bool Broken => !Exists;
    public override string Message => $"No node '{NodeId}' in the scene.";
}
