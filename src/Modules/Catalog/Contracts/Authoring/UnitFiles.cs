namespace IntoChat.OS.Modules.Apps;

// A unit's source files: its C#, plus spec.md and scenarios.json that travel with it but do not compile.
public static class UnitFiles
{
    public const string Spec = "spec.md";
    public const string Scenarios = "scenarios.json";

    public static IReadOnlyDictionary<string, string> Code(IReadOnlyDictionary<string, string> files)
        => files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);
}
