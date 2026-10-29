using DigitalBrain.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class ShellAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> WorkspaceOperations => AppBaseline.Cases(typeof(WorkspaceApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> DesktopOperations => AppBaseline.Cases(typeof(DesktopApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> SettingsOperations => AppBaseline.Cases(typeof(SettingsApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> InoOperations
        => AppBaseline.Cases(typeof(InoApp), os => os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>());

    public static IEnumerable<TheoryDataRow<AppOperationCase>> PostOperations => AppBaseline.Cases(typeof(PostApp));

    public static IEnumerable<TheoryDataRow<AppOperationCase>> CreatorOperations
        => AppBaseline.Cases(typeof(CreatorApp), os => os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>());

    [Theory, MemberData(nameof(WorkspaceOperations))]
    public Task WorkspaceBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(DesktopOperations))]
    public Task DesktopBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(SettingsOperations))]
    public Task SettingsBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(InoOperations))]
    public Task InoBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(PostOperations))]
    public Task PostBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Theory, MemberData(nameof(CreatorOperations))]
    public Task CreatorBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

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
    public async Task SettingsInoAndPostBddScenariosAndSystemNeuronsPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var settingsReport = await BddRunner.VerifyAsync<SettingsApp>(cancellationToken: ct);
        Assert.True(settingsReport.Passed);
        Assert.Single(settingsReport.Scenarios);

        var inoReport = await BddRunner.VerifyAsync<InoApp>(fakes: os => os.Register<IntoChat.OS.Modules.AI.Memory.MemoryNeuron>(), cancellationToken: ct);
        Assert.True(inoReport.Passed);
        Assert.Single(inoReport.Scenarios);

        var postReport = await BddRunner.VerifyAsync<PostApp>(cancellationToken: ct);
        Assert.True(postReport.Passed);
        Assert.Single(postReport.Scenarios);

        var creatorReport = await BddRunner.VerifyAsync<CreatorApp>(
            fakes: os => os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>(),
            cancellationToken: ct);
        Assert.True(creatorReport.Passed);
        Assert.Single(creatorReport.Scenarios);

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

        var inoInstalled = await os.Install<InoApp>();
        await settings.SetProfile("ElonWatcher", "dark", "qwen3.5:4b");
        await os.Idle();
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("Synced settings: ElonWatcher (qwen3.5:4b)", StringComparison.Ordinal));

        var uiIdentity = await settings.SetUiIdentity("spatial-glass", "compact-pill");
        Assert.Equal("spatial-glass", uiIdentity.StylePreset);
        Assert.Equal("compact-pill", uiIdentity.BarLayout);

        await inoInstalled.Request("send", new InoSend("Draft confirmation card"));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("neuron://local/os.confirmations/inbox", StringComparison.Ordinal));
        await inoInstalled.Request("approve-card", new InoApproveCard("card-1", true));
        Assert.Contains(inoInstalled.Main.Texts, text => text.Contains("approved", StringComparison.Ordinal));

        os.Register<IntoChat.OS.Modules.Microsoft.Aspire.AspireNeuron>();
        var creatorInstalled = await os.Install<CreatorApp>();
        await creatorInstalled.Request("open");
        await creatorInstalled.Request("prompt", new CreatorPrompt("Track Elon Musk posts", "post"));
        Assert.Contains(creatorInstalled.Main.Texts, text => text.Contains("3D BrainScene", StringComparison.Ordinal));
    }
}
