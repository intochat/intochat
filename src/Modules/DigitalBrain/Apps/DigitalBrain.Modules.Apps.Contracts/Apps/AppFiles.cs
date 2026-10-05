namespace DigitalBrain.Apps;

public static class AppFiles
{
    public const string Runtime = "csharp";
    public const string TestsPath = "tests.cs";
    public const string SpecPath = "app.spec.md";
    public const string BehaviorsPrefix = "behaviors/";

    public static bool IsBehavior(string path)
        => path.StartsWith(BehaviorsPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal);
}
