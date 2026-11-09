using DigitalBrain.Invocation;
using DigitalBrain.UI;
using Orleans;
using Orleans.CodeGeneration;

namespace IntoChat.OS.Modules.Apps.Facets;

[Alias("apps.view-facet")]
[Version(1)]
public interface IViewFacet : IGrainExtension
{
    Task<string> RenderMainJsonAsync();
    Task<string?> RenderSideJsonAsync();
    Task<string> PresentJsonAsync(long expectedProgramRevision, string presentationJson);
}
