using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.OS.Testing.Workspace;
using Microsoft.Playwright;

namespace DigitalBrain.OS.Testing;

// A fact on the shared host: xUnit injects the assembly fixture, the lease gives the fact its own
// workspace and lifetime. A fact that needs another composition does not inherit this; it
// boots its own host through ReferenceBrain.
public abstract class BrainFact(ReferenceBrainFixture host) : IAsyncLifetime
{
    private ITestBrain? _brain;

    protected ReferenceBrainFixture Host { get; } = host;
    protected ITestBrain Brain => _brain ?? throw new InvalidOperationException("The lease is taken in InitializeAsync.");

    // Fresh per fact, so facts sharing one host address their own workspace.
    protected string WorkspaceId { get; } = "workspace-" + Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _brain = await Host.LeaseAsync(ct);
        await UserConnections.AuthorizeDatabaseAsync(_brain, WorkspaceId, ct);
        await ResetShellStateAsync(_brain.Owner, ct);
    }

    // The shell snapshot (project list, selection) is one deployment-global document; starting
    // every fact from an empty shell keeps browser facts as isolated as on a private host.
    private static async Task ResetShellStateAsync(Person owner, CancellationToken ct)
    {
        var current = await owner.Http.GetFromJsonAsync<JsonElement>("/shell/state", ct);
        var revision = current.GetProperty("revision").GetInt32();
        if (revision == 0) { return; }
        using var reset = await owner.Http.PutAsJsonAsync("/shell/state", new
        {
            expectedRevision = revision,
            operationId = Guid.NewGuid().ToString(),
            snapshot = new { version = 1, projects = Array.Empty<object>(), selectedProjectId = (string?)null, settings = new { } },
        }, ct);
        reset.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        if (_brain is not null) { await _brain.DisposeAsync(); }
    }

    // A fresh browser context against the shared web shell; it closes with the fact's lease.
    protected async Task<IPage> OpenPageAsync(CancellationToken cancellationToken)
        => (await Brain.OpenBrowserAsync(ReferenceBrainFixture.Browser, cancellationToken)).Page;
}
