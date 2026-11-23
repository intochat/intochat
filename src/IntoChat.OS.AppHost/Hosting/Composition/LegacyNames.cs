using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.Aspire.Hosting;

// Data volumes are named after the resource that owns them, so a renamed resource would open an
// empty volume. These names are the ones the resources had before the rename and stay frozen.
internal static class LegacyNames
{
    public const string Root = "Modules";

    public static string Resource(string name) => $"{Root}-{name}";

    public static string DataVolume(IDistributedApplicationBuilder builder, string legacyResourceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return VolumeNameGenerator.Generate(builder.CreateResourceBuilder(new ContainerResource(legacyResourceName)), "data");
    }
}
