using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.LocalSetup;

// The only file in this library that spells configuration keys; everything else speaks through these members.
public static class LocalSetupKeys
{
    public const string Topology = IntoChatOSNames.TopologyKey;
    public const string ConfigFile = "IntoChatOS:ConfigFile";
    public const string ShellEnabled = IntoChatOSNames.ShellEnabledKey;
    public const string ModelOverride = IntoChatOSNames.LocalModelKey;
    public const string LocalOllamaEndpoint = IntoChatOSNames.LocalOllamaEndpoint;
    public const string LocalTopology = IntoChatOSNames.TopologyLocal;

    public static string Argument(string key, string value) => $"--{key}={value}";
}
