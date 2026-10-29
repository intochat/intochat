using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ShellAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> WorkspaceOperations => AppBaseline.Cases(typeof(WorkspaceApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> DesktopOperations => AppBaseline.Cases(typeof(DesktopApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> SettingsOperations => AppBaseline.Cases(typeof(SettingsApp));

    [Theory, MemberData(nameof(WorkspaceOperations))]
    public Task WorkspaceBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(DesktopOperations))]
    public Task DesktopBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(SettingsOperations))]
    public Task SettingsBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public void SettingsCarriesTheFlutterTreesAndTheAppsLibrary()
    {
        var bundle = SettingsApp.Bundle;
        Assert.Equal("settings", bundle.Name);
        Assert.Contains("No modules are loaded.", bundle.Files["SettingsView.cs"], StringComparison.Ordinal);
        Assert.Contains("/apps-view", bundle.Files["Apps.cs"], StringComparison.Ordinal);
        Assert.Contains("apps-connections.cs", bundle.Files.Keys);
        Assert.DoesNotContain(bundle.Files.Keys, path => path.Equals("open.cs", StringComparison.Ordinal) && bundle.Files[path].Contains("Apps.Present", StringComparison.Ordinal));
        Assert.Equal("workspace", WorkspaceApp.Bundle.Name);
        Assert.Equal("desktop", DesktopApp.Bundle.Name);
        Assert.Contains("Apps", DesktopApp.Bundle.Files["DesktopLayout.cs"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings", "cloud", true)]
    [InlineData("settings", " local ", false)]
    [InlineData("settings", "cloud ", true)]
    [InlineData("workspace", "cloud", false)]
    [InlineData("settings", null, false)]
    public void TheSettingsAppWritesTheAccountSurfaceOnlyOnACloudHost(string app, string? topology, bool writes)
        => Assert.Equal(writes, AppNeuron.WritesAccountSurface(app, topology));

    [Fact]
    public void ModuleListNamesTheAppsModuleAndNoSecretFields()
    {
        _ = typeof(AppsModule);
        var rows = InstalledModules.List();
        var apps = Assert.Single(rows, row => row.Id == "apps");
        Assert.Equal(nameof(AppsModule), apps.Name);
        var json = InstalledModules.Json();
        Assert.Contains("\"id\":\"apps\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"secret\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"account\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsBddScenarioAndSystemNeuronsPass()
    {
        var report = await BddRunner.VerifyAsync<SettingsApp>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);

        await using var os = TestOS.Local();
        os.Register<UserSettingsNeuron>();
        os.Register<ProfileNeuron>();
        var settings = os.Get<IUserSettings>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.settings", "settings"));
        var updated = await settings.SetProfile("Vlad", "light", "gpt-5");
        Assert.Equal("Vlad", updated.DisplayName);
        Assert.Equal("light", updated.Theme);
        Assert.Equal("gpt-5", updated.DefaultModel);

        var profile = os.Get<IProfile>(new DigitalBrain.NeuronId(new DigitalBrain.BrainId("local"), "os.profile", "profile"));
        var pinned = await profile.SetBio("vlad", "AI OS Architect", ["creator"]);
        Assert.Equal("vlad", pinned.Handle);
        Assert.Equal("AI OS Architect", pinned.Bio);
        Assert.Contains("creator", pinned.PinnedApps);
    }
}
