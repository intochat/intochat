namespace DigitalBrain.Apps;

public static class AppFiles
{
    public const string Runtime = "csharp";
    public const string SpecPath = "app.spec.md";
    public const string SynapsesPrefix = "synapses/";

    public static bool IsSynapse(string path)
        => path.StartsWith(SynapsesPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal);
}
