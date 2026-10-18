namespace IntoChat.OS.Microsoft.CSharp.Sandbox;

internal sealed class SandboxOptions
{
    public const string SectionName = "Sandbox";

    // Downloaded assemblies, by content hash; shared by every session in this process.
    public string CacheRoot { get; set; } = "/cache";
    public int LogLines { get; set; } = 5000;
    // Dedicated threads each session may spend on rules; a hung rule holds its thread for good.
    public int RuleThreads { get; set; } = 2;
}
