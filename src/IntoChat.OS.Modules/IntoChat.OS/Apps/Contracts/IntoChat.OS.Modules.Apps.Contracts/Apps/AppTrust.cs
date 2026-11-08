namespace IntoChat.OS.Modules.Apps;

// An app is OS-trusted when it runs a compiled type the host loads in process (the OS's own apps such as Ino and Tasks,
// or a compiled package the owner approved as trusted code with the host's access). A model-authored app runs a program
// from sources or prebuilt behaviors and never is. Setting the compiled type swaps the app's program for that host code,
// so the setting cannot make a model-authored view trusted.
public static class AppTrust
{
    public const string CompiledTypeSetting = "__CompiledType";

    public static bool IsOsTrusted(IReadOnlyDictionary<string, string>? settings)
        => settings is not null && settings.TryGetValue(CompiledTypeSetting, out var type) && !string.IsNullOrWhiteSpace(type);

    public static bool IsOsTrusted(AppSnapshot? app) => app is not null && IsOsTrusted(app.Settings);
}
