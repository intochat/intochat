using DigitalBrain.Modules;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.UI;

// The control neurons (control.*) a screen mounts as children, the read-only legacy ui.chat reader, and the Widgetbook site.
[DigitalBrain.Modules.Module(UiModule.ModuleName)]
public sealed class UiModule : DigitalBrain.Modules.Module, IHttpModule
{
    internal const string ModuleName = "ui";

    public static System.Reflection.Assembly? Contracts => typeof(Controls.ICheckBoxControl).Assembly;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    // The Widgetbook static site at /widgetbook, beside the widgetbook neuron.
    public void Configure(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        Widgetbook.WidgetbookWebEndpoints.Map(endpoints);
    }
}
