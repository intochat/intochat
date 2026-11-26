using System.Text.Json;
using DigitalBrain.Testing.Scenarios;
using IntoChat.OS.Tests.Support;
using Microsoft.Playwright;

namespace IntoChat.OS.Tests.Browser.Scenarios;

[CollectionDefinition(nameof(WebShellCollection))]
public sealed class WebShellCollection : ICollectionFixture<WebShellHost>;

[Collection(nameof(WebShellCollection))]
public sealed class BrowserScenarioFacts(WebShellHost host)
{
    private static readonly ScenarioRecording Recording = new(TestHome.RepoRoot(), ScenarioRecording.NewRunId());

    // The exported list every client driver reads (ScenarioExportFacts keeps it equal to ShowcaseScenarios.All). Evaluated in this
    // process, the data-node scenarios found no [Showcase] examples and expected their placeholder text instead (seen live).
    private static readonly IReadOnlyList<UiScenario> Scenarios =
        ScenarioJson.Read(File.ReadAllText(Path.Combine(TestHome.RepoRoot(), "ui", "test", "scenarios", "scenarios.json")));

    // INTOCHAT_SCENARIOS=a,b runs only those scenarios (a quick subset while working on the driver).
    public static TheoryData<string> Names => [.. Scenarios.Select(scenario => scenario.Name)
        .Where(name => Environment.GetEnvironmentVariable("INTOCHAT_SCENARIOS") is not { Length: > 0 } only || only.Split(',').Contains(name))];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_showcase_scenario_passes_in_the_browser(string name)
    {
        var (verdict, steps) = await Run(name);
        var scenario = Scenarios.Single(item => item.Name == name);
        Assert.True(verdict.Passed, $"{name} failed at step {verdict.FailedStep} ({(verdict.FailedStep is { } i ? JsonSerializer.Serialize(scenario.Steps[i]) : "-")}): {verdict.Reason} [{verdict.EvidenceRef}]");
        Assert.Equal(scenario.Steps.Count, steps.Count);
        Assert.True(File.Exists(Recording.Trace(name)), $"No trace at {Recording.Trace(name)}.");
    }

    [Fact]
    public async Task Every_step_has_a_screenshot()
    {
        var name = Scenarios[0].Name;
        var (verdict, steps) = await Run(name);

        Assert.True(verdict.Passed, $"{name} failed at step {verdict.FailedStep}: {verdict.Reason}");
        Assert.Equal(Scenarios[0].Steps.Count, steps.Count);
        Assert.All(steps, step =>
        {
            Assert.Equal(Recording.EvidenceRef(name, step.Index), step.EvidenceRef);
            Assert.True(File.Exists(Path.Combine(Recording.RepoRoot, step.EvidenceRef!)), $"Step {step.Index} has no screenshot.");
        });
    }

    private async Task<(RunVerdict Verdict, IReadOnlyList<StepTaken> Steps)> Run(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var scenario = Scenarios.Single(item => item.Name == name);
        await using var context = await host.NewBrowserContextAsync(1600, 1000, ct);
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Title = name });
        var page = await context.NewPageAsync();
        // Message types only, never bodies: a page log can carry user data.
        var console = new List<string>();
        page.Console += (_, message) => console.Add("console:" + message.Type);
        page.PageError += (_, _) => console.Add("page-error");
        page.RequestFailed += (_, request) => console.Add($"request-failed:{request.Method} {new Uri(request.Url).AbsolutePath}");
        page.Response += (_, response) => { if (response.Status >= 400) { console.Add($"response:{response.Status} {new Uri(response.Url).AbsolutePath}{(response.Status == 403 ? Uri.UnescapeDataString(new Uri(response.Url).Query) : "")}"); } };
        var run = new RecordedRun();
        var driver = new BrowserDriver(page, Recording, name, BrowserLaunch.Headed(Environment.GetEnvironmentVariable));
        RunVerdict verdict;
        try
        {
            await Load(page, host.Shell);
            // The app's top-level view is drawn once any keyed node or slot is in the semantics tree.
            await page.Locator("[flt-semantics-identifier]").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 });
            verdict = await ScenarioRunner.Run(scenario, driver, run, ct);
        }
        finally
        {
            Directory.CreateDirectory(Recording.ScenarioDirectory(name));
            await page.ScreenshotAsync(new() { Path = Path.Combine(Recording.ScenarioDirectory(name), "last.png") });
            // The semantics tree the driver read, for a failing step's diagnosis.
            await File.WriteAllTextAsync(Path.Combine(Recording.ScenarioDirectory(name), "semantics.html"),
                await page.EvaluateAsync<string>("() => document.querySelector('flt-semantics-host')?.outerHTML ?? ''"), CancellationToken.None);
            await File.WriteAllLinesAsync(Path.Combine(Recording.ScenarioDirectory(name), "console.log"), console, CancellationToken.None);
            await context.Tracing.StopAsync(new() { Path = Recording.Trace(name) });
        }
        return (verdict, run.Steps);
    }

    // The dev web server can answer before its release bundle is complete, and a page loaded then stays blank: reload until the
    // shell draws its semantics tree.
    private static async Task Load(IPage page, string url)
    {
        for (var attempt = 1; ; attempt++)
        {
            await page.GotoAsync(url);
            try
            {
                await page.Locator("flt-semantics").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 8) { }
        }
    }

    // The run's journal kept in the test: the same StepTaken records the run neuron emits.
    private sealed class RecordedRun : IRun
    {
        private ScenarioRunState _state = new();
        public List<StepTaken> Steps { get; } = [];
        public Task Start(string scenario, string driver) { _state = new(scenario, driver, 0, null); return Task.CompletedTask; }
        public Task Step(int index, ScenarioStep step, string outcome, string? shown, string? evidenceRef)
        {
            Steps.Add(new StepTaken(index, step, outcome, shown, evidenceRef));
            _state = _state with { Steps = _state.Steps + 1 };
            return Task.CompletedTask;
        }
        public Task Finish(RunVerdict verdict) { _state = _state with { Verdict = verdict }; return Task.CompletedTask; }
        public Task<ScenarioRunState> Read() => Task.FromResult(_state);
    }
}

public sealed class BrowserLaunchFacts
{
    [Fact]
    public void Headed_flag_is_off_by_default()
    {
        var options = BrowserLaunch.Options(_ => null);

        Assert.True(options.Headless);
        Assert.Null(options.SlowMo);
        Assert.False(BrowserLaunch.Headed(_ => "0"));
    }

    [Fact]
    public void Headed_flag_shows_the_browser_slowed_down()
    {
        var options = BrowserLaunch.Options(name => name == BrowserLaunch.HeadedVariable ? "1" : null);

        Assert.False(options.Headless);
        Assert.Equal(250, options.SlowMo);
    }

    [Fact]
    public void Recordings_go_under_artifacts_scenario_runs_by_run_id()
    {
        var recording = new ScenarioRecording(Path.Combine("C:", "repo"), "run-1");

        Assert.Equal(Path.Combine("C:", "repo", "artifacts", "scenario-runs", "run-1", "nav-x", "trace.zip"), recording.Trace("nav-x"));
        Assert.Equal("artifacts/scenario-runs/run-1/nav-x/step-03.png", recording.EvidenceRef("nav-x", 3));
    }
}
