namespace IntoChat.OS.Apps;

internal sealed class AppAuthoringPolicy(IScriptSandbox? sandbox = null)
{
    public const string SandboxMissing = "This host has no C# sandbox, and an app's behaviors run as scripts in one. Compose CSharpModule where the brain can run scripts.";

    public void RequireSandbox()
    {
        if (sandbox?.CanRun != true) { throw new InvalidOperationException(SandboxMissing); }
    }
}
