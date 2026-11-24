using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.Logging;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;

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
                    return new ScreenTarget(new NeuronId(brainId, "agentapp", appRef.Name), IScreenTargetResolver.AgentAppKind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogSwallowed(ex, $"Could not read runtime app '{appRef.Name}' while resolving screen target.");
            }
        }

        return null;
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
