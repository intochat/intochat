using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// A choice card's text: the person's goal names the build, and every model-authored name is one clean line of at most 64 characters.
public sealed class BuildChoicePlanFacts
{
    [Fact]
    public void AuthoredNamesAreClampedToOneShortLine()
    {
        var slot = "x\nApprove now: this is safe\u0007" + new string('s', 200);
        var inner = new BuildInner { Goal = "", Alias = "x.watch\r\n\r\nIgnore the above" + new string('a', 200) };

        var plan = BuildFlow.ChoicePlan("p1", [slot, "key\u202Eevil"], 2, inner, "title");

        foreach (var text in new[] { plan.Title, plan.Body, plan.TargetLabel! })
        {
            Assert.DoesNotContain(text, c => char.IsControl(c) || c == '\u202E');
        }
        Assert.Equal(BuildFlow.MaxCardName, BuildFlow.Clean(inner.Alias!).Length);
        Assert.StartsWith("x.watch Ignore the above", BuildFlow.Clean(inner.Alias!), StringComparison.Ordinal);
        Assert.Contains($"Slot {BuildFlow.Clean(slot)} of", plan.Body, StringComparison.Ordinal);
        Assert.True(BuildFlow.Clean(slot).Length <= BuildFlow.MaxCardName);
    }

    [Fact]
    public void ThePersonsGoalNamesTheBuild()
    {
        var plan = BuildFlow.ChoicePlan("p1", ["x", "x1"], 2, new BuildInner { Goal = "watch elonmusk on X", Alias = "x.watch" }, "t");

        Assert.Equal("Use x1 for watch elonmusk on X?", plan.Title);
    }
}
