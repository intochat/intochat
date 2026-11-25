using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The ArtCraft studio page reads and saves its documents as workspace files through the hosted storage content route;
// the old /artifacts routes are gone.
public sealed class ArtCraftPageFacts
{
    [Fact]
    public void ThePageLoadsAndSavesThroughTheWorkspaceAndTheContentRoute()
    {
        foreach (var descriptor in ArtCraftCatalog.All)
        {
            var page = ArtCraftWebEndpoints.RenderInteractiveStudioHtml(descriptor, "/work/poster.png", "alice");

            Assert.DoesNotContain("/artifacts/", page, StringComparison.Ordinal);
            Assert.DoesNotContain("EventSource", page, StringComparison.Ordinal);
            Assert.Contains("const brain = \"alice\";", page, StringComparison.Ordinal);
            Assert.Contains("'/workspace/item?path='", page, StringComparison.Ordinal);
            // A save takes a ticket and uploads to the url it gives; a file that is there is replaced only on the person's answer.
            Assert.Contains("granted.url", page, StringComparison.Ordinal);
            Assert.Contains("found.overwrites", page, StringComparison.Ordinal);
            Assert.Contains("window.confirm(", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutAPersonsSessionThePageHasNoBrainAndTouchesNoFiles()
    {
        var script = ArtCraftWebEndpoints.RenderVfsBridgeScript(ArtCraftCatalog.Get("photocraft"));

        Assert.Contains("const brain = null;", script, StringComparison.Ordinal);
        Assert.Contains("if (!brain ", script, StringComparison.Ordinal);
    }
}
