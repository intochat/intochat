using System.Collections.Concurrent;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Http;
using IntoChat.OS.Host.Brains;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Host.Client;

// A brain in use is a created brain: a development owner never registers, and creating
// provisions the OS defaults into it. Every request to the brain waits for the same creation,
// so none sees a half-provisioned brain. Once per brain per host; the call is idempotent, so a
// retry after a failure is harmless.
internal sealed class OsBrainRouteInitializer(
    IIntoChatOSBrains brains,
    IBrainResidencyRegistry? residencyRegistry = null,
    ILogger<OsBrainRouteInitializer>? logger = null) : IBrainRouteInitializer
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _created = new(StringComparer.Ordinal);

    public async ValueTask InitializeAsync(string accountId, string brainId, CancellationToken cancellationToken = default)
    {
        var scope = BrainScope.Create(accountId, brainId).Id;
        var creating = _created.GetOrAdd(scope, _ => new Lazy<Task>(() =>
        {
            var brain = brains.Get<IBrain>(IBrain.Key(scope));
            return residencyRegistry is not null && residencyRegistry.TryGetEphemeral(scope, out var ephemeral)
                ? brain.CreateEphemeral(new Owner(accountId), ephemeral)
                : brain.Create(new Owner(accountId));
        }));
        // Best effort: the request itself is already allowed, and the next one retries.
        try { await creating.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var log = logger ?? OsConsoleLog.For(typeof(OsBrainRouteInitializer));
            log.LogSwallowed(error, "best-effort brain EnsureCreated");
            _created.TryRemove(new KeyValuePair<string, Lazy<Task>>(scope, creating));
        }
    }
}
