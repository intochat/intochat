using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using IntoChat.OS.UI;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class TurnkeyGitHubHubAndInoNavigationBrowserFacts(TurnkeyTriageWebShellFixture host)
    : IClassFixture<TurnkeyTriageWebShellFixture>
{
    private const int StudioViewportWidth = 1440;
    private const int StudioViewportHeight = 900;

    [Fact]
    public async Task Engineer_configure_github_deep_link_opens_settings_github_hub_adopts_policy_and_installs_synapse_at_1440x900()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = StudioViewportWidth, Height = StudioViewportHeight } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        try
        {
            // 1. Start on Engineer at 1440x900 and click Configure GitHub ↗ (engineer:open-github-settings -> Settings Connections / github)
            await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await ScrollAndClickButtonAsync(page, "engineer:open-github-settings", scrollCenterX: 1260, scrollCenterY: 420, ct);
            await page.KeyedNode("settings-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);

            var settingsShellBox = await page.MeasureKeyedBoxAsync("settings-shell", minWidth: 900f, minHeight: 700f, ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Engineer->Settings GitHub Hub Surface: {settingsShellBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(settingsShellBox.HeightRatio(StudioViewportHeight) >= 0.80f);

            // 2. Verify Connections / github hub rendered copyable webhook commands, policy governance, and 1-click Synapse installer
            await WaitForSemanticsTextContainsAsync(page, "Connections / github", ct);
            await WaitForSemanticsTextContainsAsync(page, "/integrations/github/webhook", ct);

            await EnsureKeyVisibleAsync(page, "settings:github:copy-webhook-url", scrollCenterX: 820, scrollCenterY: 450, ct);
            await EnsureKeyVisibleAsync(page, "settings:github:copy-gh-forward", scrollCenterX: 820, scrollCenterY: 450, ct);

            // 3. Click 1-click policy preset settings:github-policy:draft-only -> adopts PolicyRule and renders Draft-only (0 auto-posts)
            await ScrollAndClickButtonAsync(page, "settings:github-policy:draft-only", scrollCenterX: 820, scrollCenterY: 500, ct);
            await WaitForSemanticsTextContainsAsync(page, "Draft-only (0 auto-posts)", ct);

            // Reset policy so shared WebShellHost remains clean for sibling tests
            await ScrollAndClickButtonAsync(page, "settings:github-policy:reset", scrollCenterX: 820, scrollCenterY: 500, ct);
            await WaitForSemanticsTextContainsAsync(page, "Policy reset", ct);

            // 4. Click 1-click Synapse installer settings:github-synapse:draft -> compiles & deploys gh-triage-main
            await ScrollAndClickButtonAsync(page, "settings:github-synapse:draft", scrollCenterX: 820, scrollCenterY: 600, ct);
            await WaitForSemanticsTextContainsAsync(page, "gh-triage-main", ct);
            await EnsureKeyVisibleAsync(page, "settings:github:open-tasks", scrollCenterX: 820, scrollCenterY: 600, ct);
            await EnsureKeyVisibleAsync(page, "settings:github:open-creator", scrollCenterX: 820, scrollCenterY: 600, ct);

            // Restore Settings general section for shared WebShellHost cleanliness
            using var kernel = host.CreateKernelClient();
            using var restoreResp = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/apps/settings/request",
                new { operation = "navigate", input = """{"section":"general"}""" },
                ct);
            Assert.Equal(HttpStatusCode.Accepted, restoreResp.StatusCode);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Engineer->Settings GitHub Hub E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task Ino_navigated_to_arrival_and_engineer_ask_ino_deep_link_render_seeded_prompt_and_context_at_1440x900()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = StudioViewportWidth, Height = StudioViewportHeight } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        try
        {
            // 1. Open apps/ino/view, wait for child conversation slot + NeuronWatchMux stream to settle, then dispatch ui.navigate-to
            await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("conversation").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
            await WaitForSemanticsTextContainsAsync(page, "Send message", ct);
            await page.WaitForTimeoutAsync(600).WaitAsync(ct);

            using var kernel = host.CreateKernelClient();
            var navSignal = new UiNavigateTo(
                ArgsJson: JsonSerializer.Serialize(
                    new InoNavArgs(
                        Prompt: "Audit GitHub webhook triage rules for intochat/os",
                        FocusTarget: "focus:github/main"),
                    JsonSerializerOptions.Web),
                FromNeuron: "app.settings",
                Transition: ViewTransition.FadeThrough);

            using (var actResp = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/act",
                new
                {
                    neuron = $"{host.Scope}/apps/ino",
                    target = ".",
                    signal = UiNavigateTo.SignalAlias,
                    args = JsonSerializer.SerializeToElement(navSignal, JsonSerializerOptions.Web),
                },
                ct))
            {
                Assert.Equal(HttpStatusCode.OK, actResp.StatusCode);
            }

            await WaitForSemanticsTextContainsAsync(page, "Ready to ask: Audit GitHub webhook triage rules for intochat/os", ct);
            await WaitForSemanticsTextContainsAsync(page, "Send Prompt", ct);
            await page.KeyedNode("ino:send-draft").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode("ino:dismiss-draft").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            var inoHostBox = await MeasureRootSemanticsBoxAsync(page, ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Ino INavigatedTo<InoNavArgs> Surface: {inoHostBox.Format(StudioViewportWidth, StudioViewportHeight)}"));

            await page.ClickButtonAsync("ino:dismiss-draft", ct);
            await WaitForSemanticsNotContainsAsync(page, "Audit GitHub webhook triage rules for intochat/os", ct);

            // 2. Cross-window Engineer -> Ask Ino ↗ (engineer:ask-ino -> ui.navigate-from + ui.navigate-to -> apps/ino/view)
            await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

            await ScrollAndClickButtonAsync(page, "engineer:ask-ino", scrollCenterX: 1260, scrollCenterY: 420, ct);
            await WaitForSemanticsTextContainsAsync(page, "Ready to ask: Review mission", ct);
            await page.KeyedNode("ino:send-draft").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode("ino:dismiss-draft").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.ClickButtonAsync("ino:dismiss-draft", ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Ino INavigatedTo<InoNavArgs> Browser E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    internal static async Task EnsureKeyVisibleAsync(IPage page, string key, float scrollCenterX, float scrollCenterY, CancellationToken ct)
    {
        var locator = page.KeyedNode(key).First;
        for (var step = 0; step < 12; step++)
        {
            if (await locator.CountAsync().WaitAsync(ct) > 0 && await locator.IsVisibleAsync().WaitAsync(ct))
            {
                return;
            }

            await page.Mouse.MoveAsync(scrollCenterX, scrollCenterY).WaitAsync(ct);
            await page.Mouse.WheelAsync(0, 320).WaitAsync(ct);
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }

        await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10_000 }).WaitAsync(ct);
    }

    internal static async Task ScrollAndClickButtonAsync(IPage page, string key, float scrollCenterX, float scrollCenterY, CancellationToken ct)
    {
        await EnsureKeyVisibleAsync(page, key, scrollCenterX, scrollCenterY, ct);
        await page.ClickButtonAsync(key, ct);
    }

    internal static async Task<UiGeometryBox> MeasureRootSemanticsBoxAsync(IPage page, CancellationToken ct)
    {
        var box = await page.Locator("flt-glass-pane, body").First.BoundingBoxAsync(new() { Timeout = 10_000 }).WaitAsync(ct);
        Assert.NotNull(box);
        return new UiGeometryBox(box!.X, box.Y, box.Width, box.Height);
    }

    internal static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 80; i++)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
            if (visible.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }

        var finalText = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
        Assert.Contains(expectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task WaitForSemanticsNotContainsAsync(IPage page, string unexpectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 80; i++)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
            if (!visible.Contains(unexpectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }

        var finalText = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? ''").WaitAsync(ct);
        Assert.DoesNotContain(unexpectedSubstring, finalText, StringComparison.OrdinalIgnoreCase);
    }
}

[Collection(nameof(WebShellCollection))]
public sealed class TasksTurnkeyIssueTriageBrowserFacts(TurnkeyTriageWebShellFixture host)
    : IClassFixture<TurnkeyTriageWebShellFixture>
{
    private const int StudioViewportWidth = 1440;
    private const int StudioViewportHeight = 900;

    [Fact]
    public async Task Tasks_approvals_renders_webhook_triage_draft_buttons_approves_via_octokit_and_escalates_to_engineer_at_1440x900()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.ConnectPatAndInstallDraftTriageSynapseAsync(ct);
        var draft404 = await host.PostSignedIssueOpenedWebhookAndWaitForDraftAsync(404, ct);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = StudioViewportWidth, Height = StudioViewportHeight } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        try
        {
            // 1. Open TasksApp at 1440x900, verify full-bleed Kanban geometry, then switch to Approvals tab
            await page.GotoAsync(host.ShellFor("apps/tasks/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
            await page.KeyedNode("tasks-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
            await page.KanbanBoard(TasksApp.KanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 }).WaitAsync(ct);

            var kanbanBox = await page.MeasureNeuronBoxAsync("ui.kanban", TasksApp.KanbanKey, minWidth: 450f, minHeight: 630f, ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Tasks Triage Kanban Board: {kanbanBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(kanbanBox.HeightRatio(StudioViewportHeight) >= 0.70f && kanbanBox.Y <= 52f);

            await page.SelectTabAsync(TasksApp.TabsKey, "Approvals", ct);
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.WaitForSemanticsTextContainsAsync(page, "intochat/os #404", ct);

            var approveKey = $"tasks:approve-issue-draft:{draft404.PreviewId}";
            var dismissKey = $"tasks:dismiss-issue-draft:{draft404.PreviewId}";
            var escalateKey = $"tasks:escalate-issue:{draft404.PreviewId}";
            var openEngineerKey = $"tasks:open-engineer-issue:{draft404.PreviewId}";

            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.EnsureKeyVisibleAsync(page, approveKey, 700, 400, ct);
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.EnsureKeyVisibleAsync(page, dismissKey, 700, 400, ct);
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.EnsureKeyVisibleAsync(page, escalateKey, 700, 400, ct);

            // 2. Click Fix in Studio ↗ (tasks:escalate-issue:{id}) -> starts Engineer run and shows Open Run in Studio ↗
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.ScrollAndClickButtonAsync(page, escalateKey, 700, 400, ct);
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.WaitForSemanticsTextContainsAsync(page, "Open Run in Studio", ct);

            // 3. Click Approve & Post (tasks:approve-issue-draft:{id}) -> posts real Octokit comment + labels under Person authority!
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.ScrollAndClickButtonAsync(page, approveKey, 700, 400, ct);
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.WaitForSemanticsTextContainsAsync(page, "Approved & posted triage comment on issue #404", ct);

            var posted404 = Assert.Single(host.CreatedComments, c => c.IssueNumber == 404);
            Assert.Contains("Automated Issue Triage (`#404`)", posted404.Body, StringComparison.Ordinal);
            var labels404 = Assert.Single(host.AppliedLabels, l => l.IssueNumber == 404);
            Assert.Contains("\"triage\"", labels404.RawBody, StringComparison.Ordinal);
            Assert.Contains("\"bug\"", labels404.RawBody, StringComparison.Ordinal);

            // 4. Click Open Run in Studio ↗ (tasks:open-engineer-issue:{id}) -> cross-window navigation onto Engineer Studio at 1440x900!
            await TurnkeyGitHubHubAndInoNavigationBrowserFacts.ScrollAndClickButtonAsync(page, openEngineerKey, 700, 400, ct);
            await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);
            var engineerCanvasBox = await page.MeasureSceneCanvasBoxAsync("brain", ct);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[GEOMETRY] Tasks->Engineer Escalated Studio Canvas: {engineerCanvasBox.Format(StudioViewportWidth, StudioViewportHeight)}"));
            Assert.True(engineerCanvasBox.HeightRatio(StudioViewportHeight) >= 0.65f);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Tasks Turnkey Issue Triage Browser E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }
}
