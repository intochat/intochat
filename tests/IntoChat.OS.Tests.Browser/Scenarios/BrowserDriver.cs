using System.Text.Json;
using DigitalBrain.Testing.Scenarios;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser.Scenarios;

// How a scenario browser launches: headless unless INTOCHAT_E2E_HEADED=1, which shows the browser slowed down so a person can follow.
public static class BrowserLaunch
{
    public const string HeadedVariable = "INTOCHAT_E2E_HEADED";
    public const float HeadedSlowMo = 250;

    public static bool Headed(Func<string, string?> environment) => environment(HeadedVariable) == "1";

    public static BrowserTypeLaunchOptions Options(Func<string, string?> environment)
        => Headed(environment) ? new() { Headless = false, SlowMo = HeadedSlowMo } : new() { Headless = true };
}

// Where a run's recordings go: artifacts/scenario-runs/<run-id>/<scenario>/ holds trace.zip and step-NN.png per step.
public sealed record ScenarioRecording(string RepoRoot, string RunId)
{
    public string RunDirectory => Path.Combine(RepoRoot, "artifacts", "scenario-runs", RunId);

    public string ScenarioDirectory(string scenario) => Path.Combine(RunDirectory, Safe(scenario));

    public string Trace(string scenario) => Path.Combine(ScenarioDirectory(scenario), "trace.zip");

    public string Screenshot(string scenario, int step) => Path.Combine(ScenarioDirectory(scenario), $"step-{step:D2}.png");

    // The evidence a StepTaken carries: the screenshot path relative to the repository root, with forward slashes.
    public string EvidenceRef(string scenario, int step) => Path.GetRelativePath(RepoRoot, Screenshot(scenario, step)).Replace('\\', '/');

    public static string NewRunId() => DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

// Runs scenario steps on the web shell in a real browser. The shell draws to a canvas, so the driver works on Flutter's semantics
// DOM (?semantics=true): a slot is the element with flt-semantics-identifier "slot:<path>", a keyed node the element with
// "key:<key>" (both set by the shell). A target resolves as the kernel driver does: slot paths first, then the last segment as a
// node key within the deepest slot found. An act is a click on the control that sends the step's signal; a read is the text the
// browser shows under the target (visible elements only: rendered text, values and accessible names).
public sealed class BrowserDriver(IPage page, ScenarioRecording recording, string scenario, bool headed) : IScenarioDriver
{
    private static readonly float Timeout = 15_000;

    public string Name => "browser";

    public async Task Act(Act step, CancellationToken ct)
    {
        var scope = await Find(step.Target, ct) ?? throw new InvalidOperationException($"The browser shows no node or slot '{step.Target}' to act on.");
        var control = Control(scope, step);
        if (!await Shown(control))
        {
            throw new InvalidOperationException($"No control under '{step.Target}' sends {step.Signal}: the browser shows no {control}.");
        }
        await Outline(control);
        await control.ClickAsync(new() { Timeout = Timeout }).WaitAsync(ct);
        await page.WaitForTimeoutAsync(300).WaitAsync(ct);
    }

    public async Task<string> Read(string target, CancellationToken ct)
    {
        var scope = await Find(target, ct) ?? throw new InvalidOperationException($"The browser shows no node or slot '{target}'.");
        await Outline(scope);
        var texts = await scope.EvaluateAsync<string[]>(VisibleText);
        return string.Join("\n", texts);
    }

    public async Task<string?> Evidence(int stepIndex, CancellationToken ct)
    {
        Directory.CreateDirectory(recording.ScenarioDirectory(scenario));
        await page.ScreenshotAsync(new() { Path = recording.Screenshot(scenario, stepIndex), FullPage = true }).WaitAsync(ct);
        return recording.EvidenceRef(scenario, stepIndex);
    }

    // The control a user clicks to send the signal: a select clicks the item named by args.id, a retry the message's retry button,
    // anything else the button keyed by the signal's last segment (the UI Kit's "Append point" is keyed "append" for uikit.append).
    private static ILocator Control(ILocator scope, Act step)
    {
        var id = step.Args is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty("id", out var value) ? value.GetString() : null;
        var key = step.Signal switch
        {
            "control.select" when id is not null => id,
            "control.retry" when id is not null => "retry:" + id,
            _ => step.Signal[(step.Signal.LastIndexOf('.') + 1)..],
        };
        return scope.Locator(Keyed(key)).First;
    }

    private async Task<ILocator?> Find(string target, CancellationToken ct)
    {
        var root = page.Locator("flt-semantics-host");
        await root.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = Timeout }).WaitAsync(ct);
        if (target == ".") { return root; }
        var segments = target.Split('/');
        var scope = root;
        var path = "";
        for (var i = 0; i < segments.Length; i++)
        {
            var candidate = path.Length == 0 ? segments[i] : path + "/" + segments[i];
            var slot = page.Locator($"[flt-semantics-identifier=\"slot:{candidate}\"]");
            if (await Shown(slot))
            {
                if (i == segments.Length - 1) { return slot.First; }
                (path, scope) = (candidate, slot.First);
                continue;
            }
            if (i != segments.Length - 1) { break; }
        }
        var keyed = scope.Locator(Keyed(segments[^1]));
        return await Shown(keyed) ? keyed.First : null;
    }

    private static async Task<bool> Shown(ILocator locator)
    {
        // Flutter builds the semantics DOM a frame after it draws: give a just-opened page a moment.
        try { await locator.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 }); return true; }
        catch (TimeoutException) { return false; }
    }

    private static string Keyed(string key) => $"[flt-semantics-identifier=\"key:{key.Replace("\"", "\\\"", StringComparison.Ordinal)}\"]";

    // Headed runs outline the control the step is on, so a person can follow the run.
    private async Task Outline(ILocator target)
    {
        if (!headed) { return; }
        await page.EvaluateAsync("() => document.querySelectorAll('[data-scenario-current]').forEach(e => { e.style.outline = ''; e.removeAttribute('data-scenario-current'); })");
        await target.EvaluateAsync("e => { e.setAttribute('data-scenario-current', ''); e.style.outline = '3px solid #e5484d'; }");
    }

    // Only what is on screen: an element hidden, zero-sized or scrolled out of the page contributes nothing.
    private const string VisibleText = """
        root => {
          const texts = [];
          const shown = e => {
            if (e.checkVisibility && !e.checkVisibility({ visibilityProperty: true })) return false;
            const r = e.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0;
          };
          const add = t => { t = (t || '').trim(); if (t) texts.push(t); };
          const walk = e => {
            if (!shown(e)) return;
            add(e.getAttribute('aria-label'));
            if (e instanceof HTMLInputElement || e instanceof HTMLTextAreaElement) add(e.value);
            for (const child of e.childNodes) {
              if (child.nodeType === Node.TEXT_NODE) add(child.textContent);
              else if (child.nodeType === Node.ELEMENT_NODE) walk(child);
            }
          };
          walk(root);
          return texts;
        }
        """;
}
