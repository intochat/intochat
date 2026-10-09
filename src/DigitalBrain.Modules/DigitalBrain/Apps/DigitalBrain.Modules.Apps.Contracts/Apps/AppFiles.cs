namespace DigitalBrain.Apps;

public static class AppFiles
{
    public const string Runtime = "csharp";
    public const string SpecPath = "app.spec.md";
    public const string SynapsesPrefix = "synapses/";
    public const string LibraryPrefix = "lib/";

    public static bool IsSynapse(string path)
        => path.StartsWith(SynapsesPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal);

    // A program file is handed to the app's runtime; everything else is data the program reads.
    public static bool IsProgram(string path)
        => IsSynapse(path) || path.StartsWith(LibraryPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal);
}
