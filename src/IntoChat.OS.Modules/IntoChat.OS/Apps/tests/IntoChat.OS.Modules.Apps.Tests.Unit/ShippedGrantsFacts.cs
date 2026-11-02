using IntoChat.OS.Connections.Identity;

namespace IntoChat.OS.Modules.Apps;

public sealed class ShippedGrantsFacts
{
    [Theory]
    [InlineData("workspace")]
    [InlineData("desktop")]
    [InlineData("settings")]
    public void A_shell_app_is_granted_run_on_its_own_brain_only(string app)
    {
        var grants = ShippedGrants.For("ada", app);

        Assert.NotEmpty(grants);
        Assert.All(grants, grant =>
        {
            Assert.Equal("brain:ada", grant.Owner);
            Assert.Equal("app:ada/apps/" + app, grant.Grantee);
            Assert.Equal(GrantRelations.Run, grant.Scope);
            Assert.StartsWith("ada/", grant.Target[(grant.Target.IndexOf('/', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_workspace_reaches_the_workspace_it_was_denied()
        => Assert.Contains(ShippedGrants.For("owner", "workspace"), grant => grant.Target == "ui.workspace/owner/main");

    [Fact]
    public void Settings_reaches_the_brains_preferences()
        => Assert.Contains(ShippedGrants.For("owner", "settings"), grant => grant.Target == "textfield/owner/settings/*");

    [Fact]
    public void Other_apps_are_granted_nothing()
        => Assert.Empty(ShippedGrants.For("ada", "calculator"));
}
