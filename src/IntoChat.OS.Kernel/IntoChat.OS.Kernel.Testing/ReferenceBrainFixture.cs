using IntoChat.OS.AI;
using IntoChat.OS.AI.Models;
using IntoChat.OS.Flutter;
using IntoChat.OS.Testing.Aspire.Diagnostics;
using IntoChat.OS.Testing.Model;

namespace IntoChat.OS.Kernel.Testing;

// The reference module composition plus a web shell, an echoing model endpoint and shared Aspire dashboard. Facts isolate through their lease's WorkspaceId and
// fact-owned neuron ids; only a fact that needs a different composition boots its own host.
public class ReferenceBrainFixture : SharedAspireBrain, IAsyncLifetime
{
    // xUnit initializes the collection or class fixture before its facts run, so the boot (web shell
    // compile) never counts against a fact's timeout.
    public async ValueTask InitializeAsync() => await WarmUpAsync();

    // Browsers on the shared web shell: a starved runner needs more than Playwright's default to paint.
    public static BrowserOptions Browser { get; } = new() { AssertionTimeout = TimeSpan.FromMinutes(2) };

    private FakeModelServer? _model;

    public FakeModelServer Model => _model ?? throw new InvalidOperationException("The shared host has not booted yet.");

    protected override async Task<ITestBrain> StartHostAsync(CancellationToken cancellationToken)
    {
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
            .WithDashboardTelemetry()
            .StartAsync(cancellationToken);

    protected override async ValueTask OnDisposedAsync()
    {
        if (_model is not null) { await _model.DisposeAsync(); }
    }
}
