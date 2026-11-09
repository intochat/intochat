using DigitalBrain.UI;
using Orleans;
using Orleans.CodeGeneration;

namespace IntoChat.OS.Modules.Apps.Facets;

[Alias("apps.view-facet")]
[Version(1)]
public interface IViewFacet : IGrainExtension
{
    Task<Node> RenderMainAsync();
    Task<Node?> RenderSideAsync();
    Task<AppSnapshot> PresentAsync(long expectedProgramRevision, Presentation presentation);
}
