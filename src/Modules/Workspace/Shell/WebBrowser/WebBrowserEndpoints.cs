using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flutter.WebBrowser;

// The shell's embedded browser reports its attachment to its browser neuron: POST /brains/{id}/browsers/connect {name, port,
// sessionId}, /disconnect {name, sessionId} and /unavailable {name, reason}. A device connector, not a UI action. The name is a
// browser of this brain's app UI (AppUiEndpoints.OwnsUi). Responses: 200 delivered; 400 a missing port or session; 401 a name
// outside this brain's app UI. Dart mirror: IntoChatOSUiClient.webBrowser in ui/lib/src/core/ui_client.dart.
internal static class WebBrowserEndpoints
{
    private const string DefaultReason = "The browser is unavailable.";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var browsers = BrainRouteGroups.Brain(endpoints, "/browsers").AddEndpointFilter(ModuleRouteGuard.Guard);
        browsers.MapPost("/connect", (BrowserConnect input, INeuronProxies brain, CancellationToken ct)
            => Connect(input, brain, BrainScope.CurrentId(), ct));
        browsers.MapPost("/disconnect", (BrowserDisconnect input, INeuronProxies brain, CancellationToken ct)
            => Disconnect(input, brain, BrainScope.CurrentId(), ct));
        browsers.MapPost("/unavailable", (BrowserUnavailable input, INeuronProxies brain, CancellationToken ct)
            => Unavailable(input, brain, BrainScope.CurrentId(), ct));
    }

    internal static async Task<IResult> Connect(BrowserConnect input, INeuronProxies brain, string scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var browser = Browser(brain, scope, input.Name);
        if (input.Port is not { } port || input.SessionId is not { Length: > 0 } session)
        {
            throw new ArgumentException("A browser connection names its port and session.");
        }
        await browser.Connect(port, session).WaitAsync(ct);
        return Delivered();
    }

    internal static async Task<IResult> Disconnect(BrowserDisconnect input, INeuronProxies brain, string scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var browser = Browser(brain, scope, input.Name);
        if (input.SessionId is not { Length: > 0 } session) { throw new ArgumentException("A browser disconnection names its session."); }
        await browser.Disconnect(session).WaitAsync(ct);
        return Delivered();
    }

    internal static async Task<IResult> Unavailable(BrowserUnavailable input, INeuronProxies brain, string scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        await Browser(brain, scope, input.Name).Unavailable(input.Reason ?? DefaultReason).WaitAsync(ct);
        return Delivered();
    }

    private static IWebBrowserConnector Browser(INeuronProxies brain, string scope, string? name)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return name is { Length: > 0 } && AppUiEndpoints.OwnsUi(scope, name)
            ? brain.Get<IWebBrowserConnector>(name)
            : throw new UnauthorizedAccessException($"{name} is not a browser of this brain.");
    }

    private static IResult Delivered() => Results.Ok(new { delivered = true });

    internal sealed record BrowserConnect(string? Name, int? Port, string? SessionId);
    internal sealed record BrowserDisconnect(string? Name, string? SessionId);
    internal sealed record BrowserUnavailable(string? Name, string? Reason);
}
