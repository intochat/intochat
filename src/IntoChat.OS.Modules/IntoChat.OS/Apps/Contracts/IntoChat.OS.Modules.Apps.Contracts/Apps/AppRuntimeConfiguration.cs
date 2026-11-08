namespace IntoChat.OS.Modules.Apps;

// Configuration protocol shared by the Apps owner and the compiled apps it hosts.
public static class AppRuntimeConfiguration
{
    public static string AccountBindingKey(string slot) => "Account__" + slot;
}
