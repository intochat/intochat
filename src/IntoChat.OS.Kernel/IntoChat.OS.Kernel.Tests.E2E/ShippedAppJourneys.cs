using System.Text.RegularExpressions;
using Microsoft.Playwright;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Flutter.Chat;

namespace IntoChat.OS.Kernel.Tests.E2E;

// These assertions concern the real folder program rendered by Flutter, not just its manifest.
// API requests cover operations that the app deliberately exposes without a visible input control.
internal static class ShippedAppJourneys
{
    public static async Task ExerciseAsync(ShippedAppsFixture fixture, string name, CancellationToken ct)
    {
        using var caller = fixture.Person.AsCallerIn(fixture.Scope);
        var page = fixture.Page;
        Task Text(string text) => Assertions.Expect(page.GetByText(text, new() { Exact = false }).First).ToBeVisibleAsync();
        ILocator Button(string text) => page.GetByRole(AriaRole.Button, new() { Name = text, Exact = true });
        Task Request(string operation, string input) => fixture.App(name).Request(operation, input).WaitAsync(ct);
        switch (name)
        {
            case "calculator":
                var display = fixture.Brain.Get<IText>(fixture.Person.BrainId + "/apps/calculator/display");
                async Task Press(string key, string expected)
                {
                    await Button(key).ClickAsync();
                    await TestWait.UntilAsync(_ => display.Read(), state => state.Markdown == expected, TimeSpan.FromSeconds(10), ct);
                }
                foreach (var (key, expected) in new[] { ("2", "2"), ("+", "2+"), ("3", "2+3"), ("×", "2+3*"), ("4", "2+3*4"), ("=", "14") }) { await Press(key, expected); }
                await Assertions.Expect(page.GetByText("14", new() { Exact = true })).ToBeVisibleAsync();
                await Press("C", "");
                foreach (var (key, expected) in new[] { ("7", "7"), ("÷", "7/"), ("0", "7/0"), ("=", "Error") }) { await Press(key, expected); }
                await Assertions.Expect(page.GetByText("Error", new() { Exact = true })).ToBeVisibleAsync();
                await Press("8", "8");
                await Press("9", "89");
                await Assertions.Expect(page.GetByText("89", new() { Exact = true })).ToBeVisibleAsync();
                await Assertions.Expect(page.GetByText("Error", new() { Exact = true })).ToHaveCountAsync(0);
                break;
            case "settings":
                var connections = fixture.Brain.Get<ILayout>(fixture.Person.BrainId + "/apps/settings/connections");
                var revision = (await connections.Read()).Revision;
                await Button("Refresh connections").ClickAsync();
                await TestWait.UntilAsync(_ => connections.Read(), state => state.Revision > revision, TimeSpan.FromSeconds(15), ct);
                break;
            case "usage":
                await Text("recorded in this brain over the last 30 days");
                await Button("Refresh usage").ClickAsync();
                await Text("Amounts in USD");
                break;
            case "files":
                var filter = page.GetByRole(AriaRole.Textbox, new() { Name = "Filter names", Exact = true });
                await WorkspaceBrowser.EnterTextAsync(filter, "nonexistent-e2e-file");
                await Assertions.Expect(filter).ToHaveValueAsync("nonexistent-e2e-file");
                break;
            case "brainview": await Text("Recent behavior runs"); break;
            case "datavisualizer": await Text("No views yet. Ask the assistant to show a table or a chart."); break;
            case "ino":
                var conversation = fixture.Brain.Get<IChat>(fixture.Person.BrainId + "/apps/ino/transcript");
                var previous = (await conversation.Read()).Selection;
                Assert.NotNull(previous);
                await Button("New conversation").ClickAsync();
                var changed = await TestWait.UntilAsync(_ => conversation.Read(),
                    state => state.Selection is { } selected && selected.Agent.Length > 0 && selected.Agent != previous.Agent,
                    TimeSpan.FromSeconds(15), ct);
                Assert.True(changed.Selection!.Revision > previous.Revision);
                foreach (var (activity, mood) in new[]
                {
                    ("listening", "listening"), ("working", "thinking"), ("reading", "acting"),
                    ("completed", "speaking"), ("attention", "error"), ("idle", "idle"),
                })
                {
                    await fixture.App(name).Progress(activity, "Checking Ino presence", new() { ["mood"] = mood });
                    await TestWait.UntilAsync(_ => fixture.App(name).Read(),
                        state => state.Presentation?.Blackboard.GetValueOrDefault("mood") == mood,
                        TimeSpan.FromSeconds(15), ct);
                }
                break;
            case "router":
                await Assertions.Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Ask Jev to do something…" })).ToBeVisibleAsync();
                await Request("ask", "");
                await Text("A request has 1-32000 characters.");
                break;
            case "engineer": await Text("No requests yet."); break;
            case "selfimprove":
                await Text("No candidates yet.");
                await Request("request", "{}");
                await Text("Request refused:");
                break;
            case "memory": await Text("Type what you need in the dock's Recall field"); break;
            case "friends": await Text("Friends are the people in your circles."); break;
            case "circle":
                await Assertions.Expect(Button("Feed")).ToBeVisibleAsync();
                await Button("My profile").ClickAsync();
                await Text("Published apps. Explore an idea, then make it your own.");
                await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Share this profile") })).ToBeVisibleAsync();
                break;
            case "circleroom":
                await Text("No messages yet.");
                await Request("say", "hello from the folder E2E test");
                await Text("hello from the folder E2E test");
                break;
            case "photocraft":
                await Text("PhotoCraft tools connected");
                await Request("edit", "   ");
                await Text("Say what to make or change, in 1-");
                break;
            case "filmcraft":
                await Text("FilmCraft is not connected. Request open to connect its tools.");
                await Request("open", "");
                await Text("FilmCraft tools connected");
                break;
            case "workspace": await Text("Your workspace is clear."); break;
            default: Assert.Fail($"Add a browser journey for the shipped app '{name}'."); break;
        }
    }
}
