using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Engineer;
using IntoChat.OS.Modules.Microsoft.Playwright;
using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

[Collection(nameof(WebShellCollection))]
public sealed class EngineerAndCreatorBrowserFacts(WebShellHost host)
{
    [Fact]
    public async Task Creator_flame_camera_hud_and_time_travel_inspector_work_live_without_http_errors()
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

        await page.GotoAsync(host.ShellFor("apps/creator/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.SceneCameraHud().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // 1. Flame Camera HUD: normalize to 100%, zoom in (-> 125%), zoom out, drag pan, fit, and reset back to 100%
            await page.ResetSceneCameraAsync(ct);
            var baselineZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", baselineZoom);

            await page.ZoomSceneInAsync(ct);
            var zoomedIn = await WaitForZoomChangeAsync(page, "100%", ct);
            Assert.Equal("125%", zoomedIn);

            await page.ZoomSceneOutAsync(ct);
            await page.PanSceneByDragAsync(80, 40, ct);
            await page.FitSceneCameraAsync(ct);
            await page.ResetSceneCameraAsync(ct);
            var resetZoom = await WaitForZoomTargetAsync(page, "100%", ct);
            Assert.Equal("100%", resetZoom);

            // 2. Creator Time-Travel controls + keyboard stepping + Inspector tabs (State, Source, Signals, State Graph, Scenarios)
            await page.KeyedNode("replay-cursor-readout").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.command-bar", CreatorView.ReplayCommandBarKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.slider", CreatorView.ReplaySliderKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.tabs", CreatorView.InspectorTabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.PropertyGrid(CreatorView.InspectorStateKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            await page.StepTimeTravelBackwardAsync(ct);
            await page.StepTimeTravelForwardAsync(ct);
            await page.JumpTimeTravelFirstAsync(ct);
            await page.JumpTimeTravelLastAsync(ct);
            await page.ToggleTimeTravelModeAsync(ct);

            await page.SelectTabAsync(CreatorView.InspectorTabsKey, "Source", ct);
            await page.CodeEditor(CreatorView.InspectorSourceKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await page.SelectTabAsync(CreatorView.InspectorTabsKey, "Signals", ct);
            await page.Neuron("ui.table", CreatorView.InspectorSignalsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await page.SelectTabAsync(CreatorView.InspectorTabsKey, "State Graph", ct);
            await page.SceneCameraHud().First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.WaitForTimeoutAsync(220).WaitAsync(ct);

            await page.SelectTabAsync(CreatorView.InspectorTabsKey, "Scenarios", ct);
            await page.KeyedNode("run-scenario").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Creator & Flame E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    [Fact]
    public async Task Engineer_workbench_renders_and_drives_stepper_tabs_chat_and_code_editor_without_http_errors()
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

        await page.GotoAsync(host.ShellFor("apps/engineer/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.KeyedNode("engineer-shell").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        try
        {
            // Verify core Engineer workbench neurons: ICommandBar, IStepper, ITreeView, ITabs, IChatThread, IKanbanBoard, ITable
            await page.Neuron("ui.command-bar", EngineerApp.CommandBarKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Stepper(EngineerApp.StepperKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.tree-view", EngineerApp.WorktreeTreeKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.tabs", EngineerApp.WorkspaceTabsKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.chat", EngineerApp.BrainstormChatKey).First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 }).WaitAsync(ct);
            await page.KanbanBoard(EngineerApp.KanbanKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.Neuron("ui.table", EngineerApp.TasksTableKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            var stepperText = await page.ReadStepperTextAsync(EngineerApp.StepperKey, ct);
            Assert.Contains("Brainstorm", stepperText, StringComparison.OrdinalIgnoreCase);

            var chatText = await WaitForNeuronTextContainsAsync(page, "ui.chat", EngineerApp.BrainstormChatKey, "Architect", ct);
            Assert.Contains("Architect", chatText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Critic", chatText, StringComparison.OrdinalIgnoreCase);
            await page.SendChatMessageAsync(EngineerApp.BrainstormChatKey, "Verify cloud sandbox lease isolation", ct);
            var brainstormedChat = await WaitForNeuronTextContainsAsync(page, "ui.chat", EngineerApp.BrainstormChatKey, "Verify cloud sandbox lease isolation", ct);
            Assert.Contains("Verify cloud sandbox lease isolation", brainstormedChat, StringComparison.OrdinalIgnoreCase);

            // Approve plan -> transitions EngineerApp to implement-tasks & Code tab with syntax-highlighted ICodeEditor
            await page.ClickButtonAsync("engineer:approve-plan", ct);
            await page.CodeEditor(EngineerApp.CodeEditorKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.KeyedNode("code-highlight:csharp").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

            // Run verification -> transitions to Verification & Traces tab
            await page.ClickButtonAsync("engineer:run-verify", ct);
            await page.KeyedNode("kpi-eng-build").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            var verifyTable = await WaitForNeuronTextContainsAsync(page, "ui.table", EngineerApp.TasksTableKey, "PASS", ct);
            Assert.Contains("PASS", verifyTable, StringComparison.OrdinalIgnoreCase);

            // Switch ITabs to PR Review -> verify human PR gate & unified diff ICodeEditor
            await page.SelectTabAsync(EngineerApp.WorkspaceTabsKey, "PR Review", ct);
            await page.KeyedNode("engineer:pr:approve").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
            await page.CodeEditor(EngineerApp.CodeEditorKey).First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            var visible = await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.innerText ?? '<no-semantics>'");
            var ids = await page.ReadSemanticsIdentifiersAsync(ct);
            throw new Xunit.Sdk.XunitException(
                $"Engineer E2E failed: {ex.Message}\nHTTP errors: [{string.Join(", ", httpErrors)}]\nIdentifiers: [{string.Join(", ", ids.Take(80))}]\nVisible text:\n{visible[..Math.Min(1500, visible.Length)]}",
                ex);
        }

        Assert.Empty(httpErrors);
    }

    private static async Task<string> WaitForZoomChangeAsync(IPage page, string previousZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, previousZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    private static async Task<string> WaitForZoomTargetAsync(IPage page, string expectedZoom, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var current = await page.ReadSceneZoomTextAsync(ct);
            if (string.Equals(current, expectedZoom, StringComparison.Ordinal))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(100).WaitAsync(ct);
        }
        return await page.ReadSceneZoomTextAsync(ct);
    }

    private static async Task<string> WaitForNeuronTextContainsAsync(IPage page, string kind, string key, string expectedSubstring, CancellationToken ct)
    {
        for (var i = 0; i < 80; i++)
        {
            var current = await page.ReadNeuronTextAsync(kind, key, ct);
            if (current.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
            await page.WaitForTimeoutAsync(150).WaitAsync(ct);
        }
        return await page.ReadNeuronTextAsync(kind, key, ct);
    }
}
