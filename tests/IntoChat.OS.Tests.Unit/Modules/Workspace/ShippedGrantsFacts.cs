using IntoChat.OS.Modules.ArtCraft;
using DigitalBrain.Capabilities.Identity;

namespace IntoChat.OS.Modules.Apps;

public sealed class ShippedGrantsFacts
{
    [Theory]
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
    public void All_17_ArtCraft_apps_receive_engine_studio_and_mcp_grants_and_no_files_vfs_grant()
    {
        foreach (var descriptor in ArtCraftCatalog.All)
        {
            var grants = ShippedGrants.For("ada", descriptor.Id);
            Assert.Contains(grants, g => g.Target == "apps.artcraft-engine/ada/*");
            Assert.Contains(grants, g => g.Target == "artcraft/ada/*");
            Assert.Contains(grants, g => g.Target == "mcp/ada/*");
            // The engine reaches the workspace through the broker; files.vfs is gone.
            Assert.DoesNotContain(grants, g => g.Target.StartsWith("files.vfs/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Kernel_ArtCraft_web_endpoint_renders_interactive_studio_html_for_all_17_apps()
    {
        foreach (var descriptor in ArtCraftCatalog.All)
        {
            var html = ArtCraftWebEndpoints.RenderInteractiveStudioHtml(descriptor);
            Assert.Contains($"data-artcraft-app=\"{descriptor.Id}\"", html, StringComparison.Ordinal);
            Assert.Contains(descriptor.Title, html, StringComparison.Ordinal);
            if (!string.Equals(descriptor.Id, "artcraft", StringComparison.Ordinal))
            {
                Assert.Contains(descriptor.CliBinary, html, StringComparison.Ordinal);
            }
            Assert.Contains("window.artcraftStudio", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fifteen_ArtCraft_studios_declare_official_prebuilt_WASM_release_bundles()
    {
        var wasmStudios = ArtCraftCatalog.All.Where(static d => d.WebReleaseZipUrl is not null).ToArray();
        Assert.Equal(15, wasmStudios.Length);
        foreach (var d in wasmStudios)
        {
            Assert.NotNull(d.WebReleaseVersion);
            Assert.Equal(
                $"https://github.com/storytold/{d.Id}/releases/download/v{d.WebReleaseVersion}/{d.Id}-web-{d.WebReleaseVersion}.zip",
                d.WebReleaseZipUrl);

            var bridge = ArtCraftWebEndpoints.RenderVfsBridgeScript(d, "/work/custom-doc.png");
            Assert.Contains("data-artcraft-vfs-bridge=\"1\"", bridge, StringComparison.Ordinal);
            Assert.Contains("window.artcraftStudio", bridge, StringComparison.Ordinal);
            Assert.Contains($"const appId = \"{d.Id}\";", bridge, StringComparison.Ordinal);
            Assert.Contains("window[appId] = bridge;", bridge, StringComparison.Ordinal);
            Assert.Contains("/work/custom-doc.png", bridge, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_desktop_reaches_the_workspace_it_was_denied()
        => Assert.Contains(ShippedGrants.For("owner", "desktop"), grant => grant.Target == "ui.workspace/owner/main");

    [Fact]
    public void Settings_reaches_the_brains_preferences()
        => Assert.Contains(ShippedGrants.For("owner", "settings"), grant => grant.Target == "textfield/owner/settings/*");

    [Fact]
    public void Other_apps_are_granted_nothing()
        => Assert.Empty(ShippedGrants.For("ada", "calculator"));
}
