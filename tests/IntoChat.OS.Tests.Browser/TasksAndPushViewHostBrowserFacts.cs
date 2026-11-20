using System.Net;
using System.Net.Http.Json;
using IntoChat.OS.Apps.Tasks;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class TasksAndPushViewHostBrowserFacts(WebShellHost host)
{
    [Fact]
    public async Task Tasks_workspace_renders_and_drives_kanban_stepper_tabs_and_command_bar_without_http_errors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1920, Height = 1600 } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/tasks/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KanbanBoard(TasksApp.KanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // 1. Verify all 4 TasksApp Modules.UI neurons and seeded Kanban cards render live
            await page.Neuron("ui.command-bar", TasksApp.CommandBarKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Stepper(TasksApp.StepperKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.tabs", TasksApp.TabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.KanbanBoard(TasksApp.KanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);

            foreach (var cardId in new[] { "task-1", "task-2", "task-3" })
            {
                await page.KanbanCard(cardId).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            }

            await page.WaitForTimeoutAsync(250).WaitAsync(ct);

            var stepperInitial = await page.ReadStepperTextAsync(TasksApp.StepperKey, ct);
            Assert.Contains("Backlog", stepperInitial, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Done", stepperInitial, StringComparison.OrdinalIgnoreCase);

            // 2. Move seeded backlog card task-3 ("Verify Kanban & Stepper push sync") right -> In Progress
            await page.MoveKanbanCardRightAsync("task-3", ct);
            await WaitForSemanticsTextContainsAsync(page, "Moved 'Verify Kanban & Stepper push sync' to In Progress.", ct);
            await WaitForStepperContainsAsync(page, TasksApp.StepperKey, "Verify Kanban & Stepper push sync", ct);

            // 3. Drive IStepper stage advancement -> moves task-3 from In Progress to Review via push sync
            await page.AdvanceStepperAsync(TasksApp.StepperKey, ct);
            await WaitForSemanticsTextContainsAsync(page, "Moved 'Verify Kanban & Stepper push sync' to Review.", ct);

            // 4. Drive ITabs across List (ITable), Details, and back to Board (IKanbanBoard) via ViewHost push
            await page.SelectTabAsync(TasksApp.TabsKey, "List", ct);
            await page.Neuron("ui.table", TasksApp.TableKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            await page.SelectTabAsync(TasksApp.TabsKey, "Details", ct);
            await page.WaitForTimeoutAsync(200).WaitAsync(ct);

            await page.SelectTabAsync(TasksApp.TabsKey, "Board", ct);
            await page.KanbanBoard(TasksApp.KanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            // 5. Exercise ICommandBar quick task action ("Add Task" -> task-4 on KanbanBoard)
            await page.ClickCommandBarItemAsync(TasksApp.CommandBarKey, "Add Task", ct);
            await page.KanbanCard("task-4").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            // 6. Out-of-band server-side TasksMove (ZERO browser clicks!) -> pushes task-4 into Done
            using var kernel = host.CreateKernelClient();
            using var serverMove = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/apps/tasks/request",
                new { operation = "move", input = """{"taskId":"task-4","stage":"done"}""" },
                ct);
            Assert.Equal(HttpStatusCode.Accepted, serverMove.StatusCode);
            await WaitForSemanticsTextContainsAsync(page, "Moved 'Task #4' to Done.", ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"TasksApp E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task ViewHost_pushes_server_side_screen_updates_into_chromium_with_zero_browser_clicks()
    {
        var ct = TestContext.Current.CancellationToken;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(BrowserLaunch.Options(Environment.GetEnvironmentVariable));
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1920, Height = 1600 } });
        var page = await context.NewPageAsync();

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        await page.GotoAsync(host.ShellFor("apps/settings/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KeyedNode("settings-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
        await page.WaitForTimeoutAsync(250).WaitAsync(ct);

        try
        {
            // Mutate IScreen directly on the server (ZERO browser clicks / ZERO post-call client timers!)
            // ViewHost must receive the server-pushed ViewChanged signal over /watch and re-render automatically.
            using var kernel = host.CreateKernelClient();

            // 1. Server-side SettingsNavigate -> switches SettingsApp to Billing section in Chromium with zero browser clicks
            using var navBillingResponse = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/apps/settings/request",
                new { operation = "navigate", input = """{"section":"billing"}""" },
                ct);
            Assert.Equal(HttpStatusCode.Accepted, navBillingResponse.StatusCode);
            await WaitForSemanticsTextContainsAsync(page, "Rule-guarded monthly spend caps", ct);

            // 2. Second server-side SettingsNavigate -> restores General section cleanly with zero browser clicks
            using var navGeneralResponse = await kernel.PostAsJsonAsync(
                $"/brains/{host.Scope}/apps/settings/request",
                new { operation = "navigate", input = """{"section":"general"}""" },
                ct);
            Assert.Equal(HttpStatusCode.Accepted, navGeneralResponse.StatusCode);
            await WaitForSemanticsNotContainsAsync(page, "Rule-guarded monthly spend caps", ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Push ViewHost E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    private static async Task WaitForSemanticsTextContainsAsync(IPage page, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
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

    private static async Task WaitForSemanticsNotContainsAsync(IPage page, string unexpectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
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

    private static async Task WaitForStepperContainsAsync(IPage page, string key, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            var current = await page.ReadStepperTextAsync(key, ct);
            if (current.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }
        Assert.Contains(expectedSubstring, await page.ReadStepperTextAsync(key, ct), StringComparison.OrdinalIgnoreCase);
    }
}
