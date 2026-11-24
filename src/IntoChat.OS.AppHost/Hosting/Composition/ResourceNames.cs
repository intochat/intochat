using Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting;

// The names the dashboard shows. Module ids in configuration never change; only the shown name differs
// for the ids in Shown. The new names apply in run mode only: a publish names deployed resources after
// the resource, so in publish mode every *For method returns the name the resource had before the rename.
// Resources that persist data or parameters under their name keep the frozen
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
            "webtools" => "web",
            _ => moduleId,
        };
    }

    // Fluent UI System Icons names for the dashboard, by module id. Icons are display-only and apply in publish mode too.
    public const string RootIcon = "Home";
    public const string OsIcon = "Server";
    public const string OsGatewayIcon = "ArrowRouting";

    public static string Icon(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return moduleId switch
        {
            "ai" => "BrainCircuit",
            "aspire" => "Pulse",
            "devices" => "Phone",
            "github" => "BranchFork",
            "mcp" => "PlugConnected",
            "orleans" => "Organization",
            "sandbox" => "Box",
            "flutter" => "Desktop",
            "webtools" => "Globe",
            "postgres" or "clickhouse" => "Database",
            "playwright" => "WindowDevTools",
            "artcraft" => "Color",
            _ => "Apps",
        };
    }

    public static string Child(string moduleId, string child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(child);
        return $"{Shown(moduleId)}-{child}";
    }

    // The kernel's instances and front. One instance is named after the role; the front is "-gateway". Publish
    // keeps OperatingSystem-{instance} and OperatingSystem-front, which name the deployed compute. The Aspire
    // component cannot reference this assembly: its KernelNames (src/Components/Aspire) states the same rule, and a test on each side pins
    // the same strings.
    public const string LegacyOs = "OperatingSystem";
    public const string LegacyOsGateway = "OperatingSystem-front";

    public static string[] OsInstances(IDistributedApplicationBuilder builder, IReadOnlyList<string> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        return PickOf<string[]>(builder,
            instances.Count == 1 ? [Os] : [.. instances.Select(instance => $"{Os}-{instance}")],
            [.. instances.Select(instance => $"{LegacyOs}-{instance}")]);
    }

    public static string OsFrontFor(IDistributedApplicationBuilder builder) => Pick(builder, OsGateway, LegacyOsGateway);

    // The front's cluster and the key the aspire Integration's Fronts carries.
    public static string OsRoleFor(IDistributedApplicationBuilder builder) => Pick(builder, Os, LegacyOs);

    public static string RootFor(IDistributedApplicationBuilder builder, string legacyName)
        => Pick(builder, Root, legacyName);

    public static string NodeFor(IDistributedApplicationBuilder builder, string moduleId, string legacyName)
        => Pick(builder, Shown(moduleId), legacyName);

    public static string ChildFor(IDistributedApplicationBuilder builder, string moduleId, string child, string legacyName)
        => Pick(builder, Child(moduleId, child), legacyName);

    internal static string Pick(IDistributedApplicationBuilder builder, string runName, string legacyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyName);
        return PickOf(builder, runName, legacyName);
    }

    private static T PickOf<T>(IDistributedApplicationBuilder builder, T run, T legacy)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ExecutionContext.IsPublishMode ? legacy : run;
    }
}
