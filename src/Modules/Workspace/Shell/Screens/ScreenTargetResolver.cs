using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Units;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Logging;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;
using HostUnitAlias = IntoChat.OS.Host.Primitives.UnitAlias;

namespace IntoChat.OS.Modules.Flutter.Screens;

internal sealed class ScreenTargetResolver(
    INeuronProxies proxies,
    IEnumerable<IHostedAppTypes> hosted,
    ILogger<ScreenTargetResolver>? logger = null) : IScreenTargetResolver
{
    private readonly IHostedAppTypes[] _hosted = hosted?.ToArray() ?? [];

    private ILogger Log => logger ?? ShellConsoleLog.For<ScreenTargetResolver>();

    public async Task<ScreenTarget?> ResolveAsync(BrainId brainId, string appName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return null;
        }

        // A unit address ({brain}/{alias}, {brain}/units/{alias}, optionally ending in /view) names the unit's state neuron.
        if (appName.Contains('/', StringComparison.Ordinal) && HostUnitAlias.TryFromAddress(brainId, appName.Trim(), out var addressed))
        {
            return new ScreenTarget(addressed.StateNeuron(brainId), IScreenTargetResolver.UnitKind);
        }

        var normalized = NormalizeName(brainId, appName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (string.Equals(normalized, IScreenTargetResolver.ConfirmationsKind, StringComparison.OrdinalIgnoreCase))
        {
            return new ScreenTarget(new NeuronId(brainId, "confirmations", "default"), IScreenTargetResolver.ConfirmationsKind);
        }

        if (_hosted.Length > 0 && IsCompiledHostApp(brainId, normalized))
        {
            return new ScreenTarget(new NeuronId(brainId, "app." + normalized, "default"), IScreenTargetResolver.CompiledScreenKind);
        }

        if (HostAppRef.TryOf(brainId, normalized, out var appRef))
        {
            try
            {
                var snapshot = await proxies.Get<IApp>(PlainNeuronContracts.IdOf(typeof(IApp), appRef.Key)).Read().WaitAsync(ct);
                if (snapshot?.Manifest?.Agent is not null)
                {
                    return new ScreenTarget(new NeuronId(brainId, "app.ino", "default"), IScreenTargetResolver.AgentAppKind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, $"Could not read runtime app '{appRef.Name}' while resolving screen target.");
            }

            // A sandboxed app publishes its view to a view neuron; that neuron takes the app's commands.
            var viewId = new NeuronId(brainId, UIVocabulary.ViewType, appRef.ViewPath);
            if (await Published(() => proxies.Get<IView>(viewId).Read(), $"view of '{appRef.Name}'", ct))
            {
                return new ScreenTarget(viewId, IScreenTargetResolver.ViewKind);
            }
        }

        // An app name no app claims, but a unit with that alias has a view: the unit takes the commands.
        if (HostUnitAlias.TryParse(normalized, out var unit)
            && await Published(() => proxies.Get<IViewReader>(unit.StateNeuron(brainId)).Read(unit.Value), $"unit '{unit.Value}'", ct))
        {
            return new ScreenTarget(unit.StateNeuron(brainId), IScreenTargetResolver.UnitKind);
        }

        return null;
    }

    // Whether the read returns a view; a neuron type this brain does not host reads as none.
    private async Task<bool> Published(Func<Task<PublishedView?>> read, string what, CancellationToken ct)
    {
        try
        {
            return await read().WaitAsync(ct) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogSwallowed(ex, $"Could not read the {what} while resolving screen target.");
            return false;
        }
    }

    private bool IsCompiledHostApp(BrainId brainId, string normalized)
    {
        var key = HostAppRef.TryOf(brainId, normalized, out var appRef) ? appRef.Key : normalized;
        foreach (var item in _hosted)
        {
            if (item.IsHostLoaded(key))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeName(BrainId brainId, string raw)
    {
        var candidate = raw.Trim();
        if (candidate.EndsWith("/view", StringComparison.Ordinal))
        {
            candidate = candidate[..^5];
        }

        if (HostAppRef.TryParse(brainId, candidate, out var parsed))
        {
            return parsed.Name;
        }

        if (candidate.StartsWith("app.", StringComparison.Ordinal))
        {
            return candidate["app.".Length..];
        }

        return candidate;
    }
}
