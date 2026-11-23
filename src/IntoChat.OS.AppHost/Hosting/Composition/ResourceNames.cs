namespace IntoChat.OS.Aspire.Hosting;

// The names the dashboard shows. Module ids in configuration never change; only the shown name differs
// for the ids in Shown. Resources that persist data or parameters under their name keep the frozen
// "Modules-" form (IntoChatOSBuilder.ResourceName) and do not come through here.
public static class ResourceNames
{
    public const string Root = "intochat";
    public const string Os = "os";
    public const string OsGateway = "os-gateway";

    public static string Shown(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return moduleId switch
        {
            "flutter" => "shell",
            "webtools" => "web",
            _ => moduleId,
        };
    }

    public static string Child(string moduleId, string child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(child);
        return $"{Shown(moduleId)}-{child}";
    }
}
