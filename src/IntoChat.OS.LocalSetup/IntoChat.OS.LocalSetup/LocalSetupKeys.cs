namespace IntoChat.OS.LocalSetup;

// The only file in this library that spells configuration keys; everything else speaks through these members.
public static class LocalSetupKeys
{
    public const string Profile = "IntoChatOS:Profile";
    public const string ConfigFile = "IntoChatOS:ConfigFile";
    public const string ShellEnabled = "IntoChatOS:Shell:Enabled";
    public const string Gpu = "IntoChatOS:Local:Gpu";
    public const string ModelOverride = "IntoChatOS:Local:Model";
    public const string LocalProfileName = "local";

    public static string Argument(string key, string value) => $"--{key}={value}";
}
