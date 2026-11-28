using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Logging;
using HostAppRef = IntoChat.OS.Host.Primitives.AppRef;
using HostUnitAlias = IntoChat.OS.Host.Primitives.UnitAlias;

namespace IntoChat.OS.Modules.Flutter.Screens;

internal enum ScreenResolutionKind
{
    Screen,
    View,
    Unit,
    Confirmation,
    NotLoaded,
}

/// <summary>
/// Discriminated result of resolving a brain-scoped screen, view, unit, or confirmation address.
/// </summary>
internal sealed record ResolvedScreen(
    ScreenResolutionKind Kind,
    NeuronId? ScreenId = null,
    bool IsAgentApp = false);

/// <summary>
/// Single server-side screen/view resolution collaborator shared by <see cref="AppUiEndpoints"/>
/// and <see cref="ScreenDirectoryNeuron"/>, built on <see cref="IScreenTargetResolver"/>.
/// </summary>
internal static class ScreenViewResolver
{
    /// <summary>
    /// Resolves a brain-scoped address (app ref, screen alias, unit address, or bare app name)
    /// to a discriminated <see cref="ResolvedScreen"/> target.
    /// </summary>
    public static async Task<ResolvedScreen?> ResolveAsync(
        BrainId brainId,
        string address,
        IScreenTargetResolver? resolver,
        IHostedAppTypes? hosted,
        CancellationToken ct = default,
        IHostedNeuronTypes? hostedNeurons = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (resolver is not null)
        {
            var resolved = await resolver.ResolveAsync(brainId, address, ct);
            if (resolved is not null)
            {
                if (resolved.Kind == IScreenTargetResolver.CompiledScreenKind
                    && !AreRequiredSlotsHosted(resolved.ScreenId, hostedNeurons))
                {
                    return new ResolvedScreen(ScreenResolutionKind.NotLoaded, resolved.ScreenId);
                }
                return resolved.Kind switch
                {
                    IScreenTargetResolver.ConfirmationsKind => new ResolvedScreen(ScreenResolutionKind.Confirmation, resolved.ScreenId),
                    IScreenTargetResolver.CompiledScreenKind => new ResolvedScreen(ScreenResolutionKind.Screen, resolved.ScreenId, IsAgentApp: false),
                    IScreenTargetResolver.AgentAppKind => new ResolvedScreen(ScreenResolutionKind.Screen, resolved.ScreenId, IsAgentApp: true),
                    IScreenTargetResolver.ViewKind => new ResolvedScreen(ScreenResolutionKind.View, resolved.ScreenId),
                    IScreenTargetResolver.UnitKind => new ResolvedScreen(ScreenResolutionKind.Unit, resolved.ScreenId),
                    _ => new ResolvedScreen(ScreenResolutionKind.Screen, resolved.ScreenId),
                };
            }
        }

        if (UnitAlias(brainId.Value, address) is { } unit)
        {
            return new ResolvedScreen(ScreenResolutionKind.Unit, UnitState(brainId, unit));
        }

        if (string.Equals(address, IScreenTargetResolver.ConfirmationsKind, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedScreen(ScreenResolutionKind.Confirmation, new NeuronId(brainId, "confirmations", "default"));
        }

        if (ScreenAlias(brainId.Value, address) is { } screenAlias)
        {
            var withoutView = address.EndsWith("/view", StringComparison.Ordinal) ? address[..^5] : address;
            var appKey = AppOf(brainId.Value, withoutView);
            if (appKey is not null && !(hosted?.IsHostLoaded(appKey) ?? true))
            {
                return new ResolvedScreen(ScreenResolutionKind.NotLoaded);
            }
            var screenId = new NeuronId(brainId, screenAlias, "default");
            if (!AreRequiredSlotsHosted(screenId, hostedNeurons))
            {
                return new ResolvedScreen(ScreenResolutionKind.NotLoaded, screenId);
            }
            return new ResolvedScreen(ScreenResolutionKind.Screen, screenId);
        }

        if (!address.Contains('/', StringComparison.Ordinal)
            && (!address.Contains('.', StringComparison.Ordinal) || address.StartsWith("app.", StringComparison.Ordinal)))
        {
            var bareName = address.StartsWith("app.", StringComparison.Ordinal) ? address["app.".Length..] : address;
            var isLoaded = !HostAppRef.TryOf(brainId, bareName, out var appRef) || (hosted?.IsHostLoaded(appRef.Key) ?? true);
            if (!isLoaded)
            {
                return new ResolvedScreen(ScreenResolutionKind.NotLoaded);
            }
            var screenId = address.StartsWith("app.", StringComparison.Ordinal)
                ? new NeuronId(brainId, address, "default")
                : HostUnitAlias.TryParse(bareName, out var screenUnit)
                    ? screenUnit.ScreenNeuron(brainId)
                    : new NeuronId(brainId, "app." + bareName, "default");
            if (!AreRequiredSlotsHosted(screenId, hostedNeurons))
            {
                return new ResolvedScreen(ScreenResolutionKind.NotLoaded, screenId);
            }
            return new ResolvedScreen(ScreenResolutionKind.Screen, screenId);
        }

        return null;
    }

    /// <summary>
    /// Pre-checks whether all required <c>[Slot]</c> neuron types declared on the compiled screen class
    /// are hosted in the current runtime so Orleans never attempts an activation doomed to throw <see cref="ActivationRefusedException"/>.
    /// </summary>
    internal static bool AreRequiredSlotsHosted(NeuronId screenId, IHostedNeuronTypes? hostedNeurons)
    {
        if (hostedNeurons is null)
        {
            return true;
        }
        if (!hostedNeurons.TryGetNeuronClass(screenId.Type, out var screenClass) || screenClass is null)
        {
            return true;
        }
        return AppAvailability.Evaluate(screenClass, hostedNeurons.IsHosted, hostedNeurons.IsContractHosted) is AppAvailability.Ready;
    }

    /// <summary>
    /// Reads a synapse unit's published view via <see cref="IViewReader"/>, falling back to <see cref="IScreen"/>.
    /// </summary>
    public static async Task<PublishedView?> ReadUnitViewAsync(
        INeuronProxies brain,
        BrainId brainId,
        string unitAlias,
        ILogger log,
        CancellationToken ct = default)
    {
        var unitView = await brain.Get<IViewReader>(UnitState(brainId, unitAlias)).Read(unitAlias).WaitAsync(ct);
        if (unitView is not null)
        {
            return unitView;
        }
        try
        {
            return await brain.Get<IScreen>(new NeuronId(brainId, unitAlias, "default")).Read().WaitAsync(ct);
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException)
        {
            log.LogSwallowed(error, "Screen view for unit could not be read.");
            return null;
        }
    }

    /// <summary>
    /// Attempts to read a screen's published view, swallowing binding/missing-neuron errors and logging them.
    /// </summary>
    public static async Task<PublishedView?> TryReadScreenAsync(
        INeuronProxies brain,
        NeuronId screenId,
        ILogger log,
        string logMessage,
        CancellationToken ct = default,
        IHostedNeuronTypes? hostedNeurons = null)
    {
        if (!AreRequiredSlotsHosted(screenId, hostedNeurons))
        {
            return null;
        }
        try
        {
            return await brain.Get<IScreen>(screenId).Read().WaitAsync(ct);
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
        {
            log.LogSwallowed(error, logMessage);
            return null;
        }
    }

    /// <summary>
    /// Attempts to read a unit's published view via <see cref="IViewReader"/>, swallowing binding/missing-neuron errors.
    /// </summary>
    public static async Task<PublishedView?> TryReadUnitReaderAsync(
        INeuronProxies brain,
        BrainId brainId,
        string unitAlias,
        ILogger log,
        string logMessage,
        CancellationToken ct = default)
    {
        try
        {
            return await brain.Get<IViewReader>(UnitState(brainId, unitAlias)).Read(unitAlias).WaitAsync(ct);
        }
        catch (Exception error) when (error is InvalidOperationException or DigitalBrain.Invocation.BindingException or ArgumentException)
        {
            log.LogSwallowed(error, logMessage);
            return null;
        }
    }

    /// <summary>
    /// Returns the ordered candidate <see cref="NeuronId"/>s for acting on a screen via <see cref="ScreenDirectoryNeuron"/>.
    /// </summary>
    public static async Task<NeuronId[]> ScreenCandidatesAsync(
        BrainId brainId,
        string appId,
        string normalizedApp,
        string kind,
        IScreenTargetResolver? resolver,
        CancellationToken ct = default)
    {
        if (resolver is not null)
        {
            var resolved = await ResolveAsync(brainId, normalizedApp, resolver, hosted: null, ct);
            return resolved is { Kind: not ScreenResolutionKind.Confirmation and not ScreenResolutionKind.NotLoaded, ScreenId: { } id }
                ? [id]
                : [];
        }

        var unit = new NeuronId(brainId, normalizedApp, "default");
        if (string.Equals(kind, IScreenTargetResolver.AgentAppKind, StringComparison.Ordinal))
        {
            return [new NeuronId(brainId, "app.ino", "default")];
        }
        if (string.Equals(kind, IScreenTargetResolver.UnitKind, StringComparison.Ordinal))
        {
            return [unit];
        }
        var viewName = appId.Contains('/', StringComparison.Ordinal) ? appId : HostAppRef.Of(brainId, normalizedApp).ViewKey;
        var view = PlainNeuronContracts.IdOf(typeof(IView), viewName);
        return !normalizedApp.Contains('.', StringComparison.Ordinal) || normalizedApp.StartsWith("app.", StringComparison.Ordinal)
            ? [new NeuronId(brainId, normalizedApp.StartsWith("app.", StringComparison.Ordinal) ? normalizedApp : "app." + normalizedApp, "default"), view, unit]
            : [view, unit];
    }

    // The view's owning app, {scope}/apps/{name}.
    public static string? AppOf(string scope, string view)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        if (HostAppRef.TryParse(brainId, view, out var direct)) { return direct.Key; }
        var parts = view.Split('/');
        return parts.Length > 3
            && parts[0] == scope
            && parts[1] == "apps"
            && HostAppRef.TryOf(brainId, parts[2], out var owning)
            ? owning.Key
            : null;
    }

    public static string? ScreenAlias(string scope, string name)
    {
        if (!BrainId.IsValid(scope)) { return null; }
        var brainId = new BrainId(scope);
        var withoutView = name.EndsWith("/view", StringComparison.Ordinal) ? name[..^5] : name;
        return HostAppRef.TryParse(brainId, withoutView, out var app)
            ? "app." + app.Name
            : null;
    }

    // A unit's view is named {scope}/{alias}: one segment, a state alias rather than an app area,
    // or {scope}/units/{alias}, optionally ending in /view.
    public static string? UnitAlias(string scope, string name) =>
        name.StartsWith(scope + "/", StringComparison.Ordinal)
        && BrainId.IsValid(scope)
        && HostUnitAlias.TryFromAddress(new BrainId(scope), name, out var alias)
            ? alias.Value
            : null;

    // The alias's state neuron, {scope}/{alias}/default.
    public static NeuronId UnitState(BrainId brainId, string alias) =>
        HostUnitAlias.TryParse(alias, out var unit)
            ? unit.StateNeuron(brainId)
            : new(brainId, alias, "default");

    public static bool OwnsUi(string scope, string name) =>
        (name.StartsWith(scope + "/", StringComparison.Ordinal)
         && name.Split('/') is [_, "apps" or "images" or "applications" or "units", ..])
        || UnitAlias(scope, name) is not null;

    public static string NormalizeAppId(string brain, string app)
    {
        if (BrainId.IsValid(brain) && HostAppRef.TryParse(new BrainId(brain), app, out var appRef))
        {
            return appRef.Name;
        }
        var prefix = brain + "/units/";
        if (app.StartsWith(prefix, StringComparison.Ordinal))
        {
            return app[prefix.Length..];
        }
        prefix = brain + "/";
        if (app.StartsWith(prefix, StringComparison.Ordinal))
        {
            return app[prefix.Length..];
        }
        if (app.StartsWith("apps/", StringComparison.Ordinal))
        {
            return app[5..];
        }
        if (app.StartsWith("app.", StringComparison.Ordinal))
        {
            return app[4..];
        }
        return app;
    }
}
