namespace DigitalBrain.Apps;

internal sealed class AppAuthoringPolicy(IScriptSandbox? sandbox = null)
{
    public const string SandboxMissing = "This host has no C# sandbox, and verifying an app runs its tests as one. Compose CSharpModule where the brain can run scripts.";

    public void RequireSandbox()
    {
        if (sandbox?.CanRun != true) { throw new InvalidOperationException(SandboxMissing); }
    }
}
