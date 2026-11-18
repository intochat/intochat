using DigitalBrain.Testing.Scenarios;
using IntoChat.OS.Apps.UiKit;
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

    [Fact]
    public void Every_uikit_page_has_one_scenario()
        => Assert.Equal(UiKitPages.All.Select(page => page.Id), ShowcaseScenarios.All.Select(scenario => scenario.Name));

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ui", "pubspec.yaml"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("No repository root (ui/pubspec.yaml) above " + AppContext.BaseDirectory);
    }
}
