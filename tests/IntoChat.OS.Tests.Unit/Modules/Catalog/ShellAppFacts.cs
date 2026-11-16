using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ShellAppFacts
{
    [Theory]
    [InlineData("settings", "cloud", true)]
    [InlineData("settings", " local ", false)]
    [InlineData("settings", "cloud ", true)]
    [InlineData("desktop", "cloud", false)]
    [InlineData("settings", null, false)]
    public void TheSettingsAppWritesTheAccountSurfaceOnlyOnACloudHost(string app, string? topology, bool writes)
        => Assert.Equal(writes, AppNeuron.WritesAccountSurface(app, topology));

    [Fact]
    public void ModuleListNamesTheAppsModuleAndNoSecretFields()
    {
        _ = typeof(AppsModule);
        var rows = InstalledModulesNeuron.ListModules();
        var apps = Assert.Single(rows, row => row.Id == "apps");
        Assert.Equal(nameof(AppsModule), apps.Name);
        var json = InstalledModulesNeuron.Json();
        Assert.Contains("\"id\":\"apps\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"account\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheAppsRootIsTheSideloadDirectory()
    {
        var root = Directory.CreateTempSubdirectory("apps-root-").FullName;
        try
        {
            var disk = new Disk();
            var full = AppsDirectoryRoot.Prepare(root);
            await AppsDirectoryRoot.Allow(disk, root);
            Assert.Equal(AppsDirectoryRoot.Label, "Apps");
            Assert.Equal(full, disk.Allowed);
            Assert.True(Directory.Exists(full));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Disk : ILocalDisk
    {
        public string? Allowed { get; private set; }
        public Task<string[]> Roots() => Task.FromResult(Allowed is null ? [] : new[] { Allowed });
        public Task AllowRoot(string path) { Allowed = path; return Task.CompletedTask; }
        public Task RevokeRoot(string path) => Task.CompletedTask;
    }
}
