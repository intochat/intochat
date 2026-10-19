using IntoChat.OS.Client;

public static partial class Ino
{
    public static string Key(IntoChatOSConnection brain) => brain.Setting("App")!;
    public static string DataKey(IntoChatOSConnection brain) => DataKey(brain.Setting("ChatDataApp"), Key(brain));
    public static string DataKey(string? chatDataApp, string app) => chatDataApp is { Length: > 0 } ? chatDataApp : app;
    public static string Scope(IntoChatOSConnection brain) => brain.Setting("Brain")!;
}
