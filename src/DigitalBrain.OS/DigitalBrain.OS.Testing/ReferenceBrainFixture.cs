using DigitalBrain.AI;
using DigitalBrain.AI.Models;
using DigitalBrain.Flutter;
using DigitalBrain.Testing.Aspire.Diagnostics;
using DigitalBrain.Testing.Model;

namespace DigitalBrain.OS.Testing;

// The reference module composition plus a web shell, an echoing model endpoint and OTLP collector. Facts isolate through their lease's WorkspaceId and
// fact-owned neuron ids; only a fact that needs a different composition boots its own host.
public class ReferenceBrainFixture : SharedAspireBrain, IAsyncLifetime
{
    // xUnit initializes the assembly fixture before any fact runs, so the boot (web shell
    // compile) never counts against a fact's timeout.
    public async ValueTask InitializeAsync() => await WarmUpAsync();

    // Browsers on the shared web shell: a starved runner needs more than Playwright's default to paint.
    public static BrowserOptions Browser { get; } = new() { AssertionTimeout = TimeSpan.FromMinutes(2) };

    private FakeModelServer? _model;
    private TestTelemetryCollector? _collector;

    public FakeModelServer Model => _model ?? throw new InvalidOperationException("The shared host has not booted yet.");
    public TestTelemetryCollector Collector => _collector ?? throw new InvalidOperationException("The shared host has not booted yet.");

    protected override async Task<ITestBrain> StartHostAsync(CancellationToken cancellationToken)
    {
        _collector = TestTelemetryCollector.Start();
        _model = await FakeModelServer.StartAsync(cancellationToken);
        return await StartBrainAsync(cancellationToken);
    }

    protected virtual Task<ITestBrain> StartBrainAsync(CancellationToken cancellationToken)
        => ReferenceBrain.Create()
            .WithOptions<AIModule, AIOptions>(ai => ai.WithModelEndpoint(AiProvider.OpenAI, Model.Endpoint))
            .WithOptions<FlutterModule, FlutterModuleOptions>(flutter => flutter.RunWebApp())
            // The baked-bundle container: concurrent fixtures must not share flutter's mutable
            // build directory, which the dev web server does.
            .WithHostConfiguration(Flutter.Aspire.Hosting.FlutterHostOptions.WebContainerKey, "true")
            // A warm boot serves the content-tagged shell image in seconds; fifteen minutes
            // covers a cold runner's one-time in-docker Flutter compile and container pulls.
            // A boot that needs longer is starved or broken - fail it and say so.
            .WithStartupTimeout(TimeSpan.FromMinutes(15))
            .ExportingTelemetryTo(Collector)
            .StartAsync(cancellationToken);

    protected override async ValueTask OnDisposedAsync()
    {
        if (_model is not null) { await _model.DisposeAsync(); }
        if (_collector is not null) { await _collector.DisposeAsync(); }
    }
}
