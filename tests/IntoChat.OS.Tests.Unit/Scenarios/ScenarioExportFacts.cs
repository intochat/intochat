using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Scenarios;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Tests.Support;

namespace IntoChat.OS.Tests.Unit.Scenarios;

// The Flutter driver reads the same scenario definitions as the kernel driver: ui/test/scenarios/scenarios.json must be what
// ShowcaseScenarios.All serialises to. With INTOCHAT_REGENERATE_SCENARIOS=1 the fact rewrites the file instead.
public sealed class ScenarioExportFacts
{
    [Fact]
    public void The_exported_scenarios_are_the_showcase_scenarios()
    {
        var target = Path.Combine(Root(), "ui", "test", "scenarios", "scenarios.json");
        var generated = ScenarioJson.Write(ShowcaseScenarios.All);
        if (Environment.GetEnvironmentVariable("INTOCHAT_REGENERATE_SCENARIOS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, generated);
            return;
        }
        Assert.True(File.Exists(target), $"{target} is missing; run this fact with INTOCHAT_REGENERATE_SCENARIOS=1.");
        Assert.True(File.ReadAllText(target).ReplaceLineEndings("\n") == generated,
            "scenarios.json differs from ShowcaseScenarios.All; run this fact with INTOCHAT_REGENERATE_SCENARIOS=1.");
    }

    public static TheoryData<string> Names => [.. ShowcaseScenarios.All.Select(scenario => scenario.Name)];

    // The Flutter driver replays what the kernel showed: ui/test/scenarios/views/<scenario>.json holds the page (view and slot
    // children) before step 0 (afterIndex -1) and after every act that changed it, unindented. With INTOCHAT_REGENERATE_SCENARIOS=1 the fact rewrites it.
    [Theory]
    [MemberData(nameof(Names))]
    public async Task The_recorded_views_are_what_the_kernel_shows(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = ShowcaseScenarios.All.Single(item => item.Name == name);
        await using var os = TestOS.Local().RegisterCatalog();
        var driver = await KernelDriver.Start(os);
        var last = await driver.Snapshot();
        var steps = new List<object> { new { afterIndex = -1, view = last } };
        for (var index = 0; index < scenario.Steps.Count; index++)
        {
            if (scenario.Steps[index] is not Act act) { continue; }
            await driver.Act(act, ct);
            var view = await driver.Snapshot();
            if (JsonElement.DeepEquals(view, last)) { continue; }
            steps.Add(new { afterIndex = index, view });
            last = view;
        }
        var generated = JsonSerializer.Serialize(new { steps }) + "\n";
        var target = Path.Combine(Root(), "ui", "test", "scenarios", "views", name + ".json");
        if (Environment.GetEnvironmentVariable("INTOCHAT_REGENERATE_SCENARIOS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, generated, ct);
            return;
        }
        Assert.True(File.Exists(target), $"{target} is missing; run this fact with INTOCHAT_REGENERATE_SCENARIOS=1.");
        Assert.True((await File.ReadAllTextAsync(target, ct)).ReplaceLineEndings("\n") == generated,
            $"{target} differs from what the kernel shows; run this fact with INTOCHAT_REGENERATE_SCENARIOS=1.");
    }

    [Fact]
    public void Every_gallery_page_has_one_scenario()
        => Assert.Equal(GalleryPages.All.Select(page => page.Id), ShowcaseScenarios.All.Select(scenario => scenario.Name));

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ui", "pubspec.yaml"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("No repository root (ui/pubspec.yaml) above " + AppContext.BaseDirectory);
    }
}
