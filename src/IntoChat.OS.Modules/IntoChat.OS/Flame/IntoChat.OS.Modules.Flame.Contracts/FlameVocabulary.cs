using IntoChat.OS;

namespace IntoChat.OS.Flame;

public static class FlameVocabulary
{
    public const string SceneType = "scene";
    public const int MaxNodes = 4096;
    public const int MaxParameterBytes = 64 * 1024;
    public const int MaxTreeNodes = 256;
    public const int MaxTreeDepth = 16;
    public static readonly string[] NodeTypes =
        ["circle", "rect", "text", "sprite", "path", "particles", "group", "activity_ring", "sphere"];
}
