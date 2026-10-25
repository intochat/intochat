namespace IntoChat.OS.LocalSetup;

// The only file in this library that spells configuration keys; everything else speaks through these members.
public static class LocalSetupKeys
{
    public const string Topology = "IntoChatOS:Topology";
    public const string ConfigFile = "IntoChatOS:ConfigFile";
    public const string ShellEnabled = "IntoChatOS:Shell:Enabled";
    public const string Gpu = "IntoChatOS:Local:Gpu";
    public const string ModelOverride = "IntoChatOS:Local:Model";
    public const string LocalTopology = "local";

    public static string Argument(string key, string value) => $"--{key}={value}";
}
