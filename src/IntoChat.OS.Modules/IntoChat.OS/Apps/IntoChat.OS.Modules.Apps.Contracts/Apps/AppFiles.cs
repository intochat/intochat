namespace IntoChat.OS.Apps;

// An app is a folder of .cs files. app.cs declares the manifest (its leading comment is the spec);
// every other .cs file is program: a synapse when it has top-level statements, a shared class otherwise.
// Any other file is data the program reads.
public static class AppFiles
{
    public const string Runtime = "csharp";
    public const string DeclarationPath = "app.cs";

    public static bool IsProgram(string path)
        => path.EndsWith(".cs", StringComparison.Ordinal) && path != DeclarationPath;
}
