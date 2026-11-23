using Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting;

// The names the dashboard shows. Module ids in configuration never change; only the shown name differs
// for the ids in Shown. The new names apply in run mode only: a publish names deployed resources after
// the resource, so in publish mode every *For method returns the name the resource had before the rename. Resources that persist data or parameters under their name keep the frozen
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

    public static string RootFor(IDistributedApplicationBuilder builder, string legacyName)
        => Pick(builder, Root, legacyName);

    public static string NodeFor(IDistributedApplicationBuilder builder, string moduleId, string legacyName)
        => Pick(builder, Shown(moduleId), legacyName);

    public static string ChildFor(IDistributedApplicationBuilder builder, string moduleId, string child, string legacyName)
        => Pick(builder, Child(moduleId, child), legacyName);

    private static string Pick(IDistributedApplicationBuilder builder, string runName, string legacyName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyName);
        return builder.ExecutionContext.IsPublishMode ? legacyName : runName;
    }
}
