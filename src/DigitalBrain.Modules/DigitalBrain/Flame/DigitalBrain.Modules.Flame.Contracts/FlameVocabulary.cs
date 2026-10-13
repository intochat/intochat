using DigitalBrain;

namespace DigitalBrain.Flame;

public static class FlameVocabulary
{
    public const string SceneType = "scene";
    public const int MaxNodes = 4096;
    public const int MaxParameterBytes = 64 * 1024;
    public static readonly string[] NodeTypes =
        ["circle", "rect", "text", "sprite", "path", "particles", "group", "ino_sphere", "brain_graph", "activity_ring"];
}
