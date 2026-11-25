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
            new ExpectShown(page.Id),
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
            new ExpectShown(page.Id),
            new Expect(page.Id, "Live points"),
            new Expect(page.Id, UiKitPages.ChartSeed[^1].Label),
            new Act(".", "uikit.append", null),
            new Expect(page.Id, UiKitPages.Point(1).Label),
        ]),
        // The nav lists every title, so a title alone does not prove the page opened: a control or scenario page is also expected to
        // show its own slot (only the open page draws it), a data node page a text of its own example that the nav does not show.
        _ when page.Group == UiKitPages.DataNode => new(page.Id, UiKitApp.AppName, [Open(page.Id), new Expect(".", page.Title), new Expect(".", ExampleText(page))]),
        _ => new(page.Id, UiKitApp.AppName, [Open(page.Id), new Expect(".", page.Title), new ExpectShown(page.Id)]),
    };

    // Opening a page is what a user does: select it in the nav.
    private static Act Open(string page) => new(UiKitPages.Nav, "control.select", Args(new { id = page }));

    private static string[] Shown => ["value", "text", "title", "label", "body", "content", "subtitle", "placeholder", "caption", "description", "message", "name"];

    // The first text a data node page's own example shows that no nav entry contains, read from the example the page renders.
    private static string ExampleText(UiKitPage page)
    {
        var examples = UiKitPages.Examples(page.Id, UiKitPages.NodeKinds[page.Id]);
        if (examples.Count == 0) { return $"The {page.Id} node has no [Showcase] example yet."; }
        string[] nav = [.. UiKitPages.All.SelectMany(item => new[] { item.Id, item.Title, item.Group })];
        var texts = examples.SelectMany(node => Strings(JsonSerializer.SerializeToElement(node, JsonSerializerOptions.Web), null)).ToArray();
        return texts.Where(text => text.Name is { } name && Shown.Contains(name, StringComparer.OrdinalIgnoreCase)).Concat(texts)
            .Select(text => Drawn(text.Value))
            .FirstOrDefault(text => text.Length >= 3 && !nav.Any(entry => entry.Contains(text, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException($"The {page.Id} example shows no text of its own.");
    }

    // What a client draws of a text: its first line without markdown marks (a markdown example draws "## Notes" as "Notes").
    private static string Drawn(string text) => text.Split('\n')[0].TrimStart('#', '-', '*', '>', ' ').Trim();

    // A node marked not visible draws none of its texts.
    private static IEnumerable<(string? Name, string Value)> Strings(JsonElement json, string? name) => json.ValueKind switch
    {
        JsonValueKind.Object when json.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False => [],
        JsonValueKind.Object => json.EnumerateObject()
            .Where(property => property.Name is not ("kind" or "key" or "id" or "icon" or "tone" or "command" or "args" or "intent" or "resolve" or "onSubmit" or "automationName"))
            .SelectMany(property => Strings(property.Value, property.Name)),
        JsonValueKind.Array => json.EnumerateArray().SelectMany(item => Strings(item, name)),
        JsonValueKind.String when json.GetString() is { Length: > 0 } value => [(name, value)],
        _ => [],
    };

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
