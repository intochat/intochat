namespace DigitalBrain.Apps;

public static class AppFiles
{
    public const string Runtime = "csharp";
    public const string TestsPath = "tests.cs";
    public const string SpecPath = "app.spec.md";
    public const string SynapsesPrefix = "synapses/";
    // Scratch verification copies carry this marker in their name and stay out of the index.
    public const string VerifyMarker = "--verify-";

    public static bool IsSynapse(string path)
        => path.StartsWith(SynapsesPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal);
}
