using System.Text.Json;
using DigitalBrain.Testing.Scenarios;
using IntoChat.OS.Apps.UiKit;

namespace IntoChat.OS.Tests.Support;

// The UI Kit scenarios every driver runs: one per page. They live here (not in a test project) so the export fact and the kernel
// driver in IntoChat.OS.Tests.Unit, the E2E tier and the Flutter driver (through ui/test/scenarios/scenarios.json) share one list.
public static class ShowcaseScenarios
{
    public static IReadOnlyList<UiScenario> All { get; } = [.. UiKitPages.All.Select(For)];

    private static UiScenario For(UiKitPage page) => page.Id switch
    {
        UiKitPages.FailedReply => new(page.Id, UiKitApp.AppName,
        [
            Open(page.Id),
            new Expect(".", page.Title),
            new Expect(page.Id, "Summarise my week"),
            new Expect(page.Id, "The model did not answer."),
            new Expect(page.Id, "Try again"),
            new Act(page.Id, "control.retry", Args(new { id = "f2" })),
            new Expect(".", "control.retry"),
        ]),
        UiKitPages.LiveChart => new(page.Id, UiKitApp.AppName,
        [
            Open(page.Id),
            new Expect(".", page.Title),
            new Expect(page.Id, "Live points"),
            new Expect(page.Id, UiKitPages.ChartSeed[^1].Label),
            new Act(".", "uikit.append", null),
            new Expect(page.Id, UiKitPages.Point(1).Label),
        ]),
        // The nav lists every title, so a title alone does not prove the page opened: a page that slots its own control is also
        // read by that slot's key, which only the open page draws (an empty Contains holds whenever the target is shown).
        _ when page.Group != UiKitPages.DataNode => new(page.Id, UiKitApp.AppName, [Open(page.Id), new Expect(".", page.Title), new Expect(page.Id, "")]),
        _ => new(page.Id, UiKitApp.AppName, [Open(page.Id), new Expect(".", page.Title)]),
    };

    // Opening a page is what a user does: select it in the nav.
    private static Act Open(string page) => new(UiKitPages.Nav, "control.select", Args(new { id = page }));

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
