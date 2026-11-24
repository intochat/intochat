namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ShellAppFacts
{
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
}
