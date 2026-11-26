using IntoChat.OS.Tests.Browser.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser;

/// <summary>
/// Aspire + Playwright browser E2E scenarios for Ino v2 (S10 Flying 3D Lottie Orb,
/// S12c Transparent Companion Orb, and Live Desktop PresenceLayer).
///
/// Run visibly in Chromium:
///   $env:INTOCHAT_E2E_HEADED="1"
///   dotnet test -c Release --project tests\IntoChat.OS.Tests.Browser\IntoChat.OS.Tests.Browser.csproj --filter-class "*InoV2BrowserFacts*"
/// </summary>
[Collection(nameof(WebShellCollection))]
public sealed class InoV2BrowserFacts(WebShellHost host)
{
    [Fact]
    public async Task Scenario1_Widgetbook_InoOrbV2_flies_and_reacts_to_controls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await OpenBrowserAsync(ct);
        var page = harness.Page;

        await OpenWidgetbookUseCaseAsync(page, "Ino Orb v2 (S10 Flying 3D Lottie Sphere)", ct);

        await page.Locator("[flt-semantics-identifier='ino:orb-v2']").First
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 })
            .WaitAsync(ct);

        await page.Locator("[flt-semantics-identifier='ino:fly-top-left']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Inspecting Auth Tree")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        await page.Locator("[flt-semantics-identifier='ino:fly-center']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "System 1 Venn")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        await page.Locator("[flt-semantics-identifier='ino:fly-bottom-right']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Spawning Live Neuron")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        await page.Locator("[flt-semantics-identifier='ino:orb-v2']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Voice-to-Voice Active")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        Assert.Empty(harness.HttpErrors);
    }

    [Fact]
    public async Task Scenario2_Widgetbook_InoCompanionOrb_expands_and_drives_quick_actions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await OpenBrowserAsync(ct);
        var page = harness.Page;

        await OpenWidgetbookUseCaseAsync(page, "Ino Companion Orb (S12c Floating Transparent)", ct);

        var orb = page.Locator("[flt-semantics-identifier='ino:companion-orb']").First;
        await orb.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);

        // Tap orb to expand transparent quick-action pill bar.
        await orb.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await page.Locator("[flt-semantics-identifier='ino:companion-actions']").First
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        // 1. Code Tree action (S12a Roslyn lazy tree).
        await page.Locator("[flt-semantics-identifier='ino:action:tree']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Expanded Auth slice")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        // 2. Live Neuron action (S12b Collectible ALC).
        await page.Locator("[flt-semantics-identifier='ino:action:neuron']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Compiled & hot-loaded neuron")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        // 3. Voice action (S11 System 1).
        await page.Locator("[flt-semantics-identifier='ino:action:voice']").First.ClickAsync(new() { Force = true }).WaitAsync(ct);
        await SemanticsText(page, "Listening to voice")
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15_000 }).WaitAsync(ct);

        Assert.Empty(harness.HttpErrors);
    }

    [Fact]
    public async Task Scenario3_DesktopShell_and_InoApp_boot_cleanly_on_Aspire_host()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await OpenBrowserAsync(ct);
        var page = harness.Page;

        // 1. Full desktop shell with live presence stream.
        await page.GotoAsync(host.Shell, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.Locator("flt-semantics-host").First
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        // 2. Open Ino app directly on the live Aspire kernel.
        await page.GotoAsync(host.ShellFor("apps/ino/view"), new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        await page.Locator("flt-semantics-host").First
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);

        Assert.Empty(harness.HttpErrors);
    }

    private static ILocator SemanticsText(IPage page, string substring)
        => page.Locator($"flt-semantics[aria-label*='{substring}'], flt-semantics:has-text('{substring}')").First;

    private async Task OpenWidgetbookUseCaseAsync(IPage page, string useCaseName, CancellationToken ct)
    {
        await page.GotoAsync($"{host.Origin}/widgetbook/", new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);

        // Flutter web places flt-semantics-placeholder offscreen (-1px,-1px); activate via DOM click.
        var placeholder = page.Locator("flt-semantics-placeholder").First;
        await placeholder.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 }).WaitAsync(ct);
        await page.EvaluateAsync("() => document.querySelector('flt-semantics-placeholder')?.click()").WaitAsync(ct);

        await page.Locator("flt-semantics-host").First
            .WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);

        // Expand Foundations -> WinForms Interaction & Motion if collapsed, then pick the use case.
        var useCase = page.GetByText(useCaseName, new() { Exact = false }).First;
        if (await useCase.CountAsync() == 0)
        {
            var foundations = page.GetByText("Foundations", new() { Exact = false }).First;
            if (await foundations.CountAsync() > 0)
            {
                await foundations.ClickAsync(new() { Force = true }).WaitAsync(ct);
            }

            var interaction = page.GetByText("WinForms Interaction & Motion", new() { Exact = false }).First;
            if (await interaction.CountAsync() > 0)
            {
                await interaction.ClickAsync(new() { Force = true }).WaitAsync(ct);
            }
        }

        await useCase.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 }).WaitAsync(ct);
        await useCase.ClickAsync(new() { Force = true }).WaitAsync(ct);
    }

    private async Task<BrowserHarness> OpenBrowserAsync(CancellationToken ct)
    {
        var context = await host.NewBrowserContextAsync(1440, 960, ct);
        var page = await context.NewPageAsync().WaitAsync(ct);

        var httpErrors = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                httpErrors.Add($"{response.Status} {new Uri(response.Url).AbsolutePath}");
            }
        };

        return new BrowserHarness(context, page, httpErrors);
    }

    private sealed class BrowserHarness(
        IBrowserContext context,
        IPage page,
        List<string> httpErrors) : IAsyncDisposable
    {
        public IPage Page => page;
        public List<string> HttpErrors => httpErrors;

        public async ValueTask DisposeAsync()
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
    }
}
