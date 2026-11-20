using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Host.Visuals;

namespace IntoChat.OS.Tests.Unit.Framework.Visuals;

public sealed class VisualCatalogNodeEmissionFacts
{
    [Fact]
    public void VisualCatalogEmitsIconLabelAndHexAccentForEveryAllowlistedKeyAndApproval()
    {
        foreach (var iconKey in VisualCatalog.AllIconKeys)
        {
            var visual = VisualCatalog.ForContract(iconKey);
            Assert.Equal(iconKey, visual.IconKey);
            Assert.False(string.IsNullOrWhiteSpace(visual.Label));
            Assert.StartsWith("#", visual.AccentHex, StringComparison.Ordinal);
        }

        var githubApproval = VisualCatalog.ForApproval("change-42");
        Assert.Equal(("GitHub", "brand:github"), (githubApproval.Label, githubApproval.IconKey));
        Assert.StartsWith("#", githubApproval.AccentHex, StringComparison.Ordinal);

        var genericApproval = VisualCatalog.ForApproval(null);
        Assert.Equal("capability:shield", genericApproval.IconKey);
        Assert.StartsWith("#", genericApproval.AccentHex, StringComparison.Ordinal);

        var (activityBrand, detail) = InoView.ResolveActivityPresentation(
            new InoActivity("act-1", "IAspire.GetResourcesAsync", "3 healthy"));
        Assert.Equal("Aspire", activityBrand.Label);
        Assert.Equal("brand:aspire", activityBrand.IconKey);
        Assert.Equal("#512BD4", activityBrand.AccentHex);
        Assert.Equal("Resources · 3 healthy", detail);
    }
}
