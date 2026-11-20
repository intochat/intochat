using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.UI;

/// <summary>
/// Canonical view and shell transition modes rendered across IntoChat OS screens and 3D graph stages.
/// </summary>
public static class ViewTransition
{
    /// <summary>Instant cut with no animation.</summary>
    public const string None = "none";

    /// <summary>Material fade-through opacity + subtle scale transition.</summary>
    public const string FadeThrough = "fade-through";

    /// <summary>Horizontal shared-axis slide + fade transition.</summary>
    public const string SharedAxisX = "shared-axis-x";

    /// <summary>Depth shared-axis scale + fade transition.</summary>
    public const string SharedAxisZ = "shared-axis-z";

    /// <summary>Smooth 3D camera fly-to transition across graph targets.</summary>
    public const string CameraFlyTo = "camera-fly-to";

    /// <summary>Returns true when <paramref name="transition"/> is empty or one of the canonical transition tokens.</summary>
    public static bool IsValid(string? transition)
        => string.IsNullOrEmpty(transition)
            || transition is None or FadeThrough or SharedAxisX or SharedAxisZ or CameraFlyTo;

    /// <summary>Normalizes a transition string to a known token (defaulting unknown values to <see cref="FadeThrough"/> and empty/whitespace to <see cref="None"/>).</summary>
    public static string Normalize(string? transition)
    {
        if (string.IsNullOrWhiteSpace(transition))
        {
            return None;
        }

        return transition.Trim() switch
        {
            None => None,
            FadeThrough => FadeThrough,
            SharedAxisX => SharedAxisX,
            SharedAxisZ => SharedAxisZ,
            CameraFlyTo => CameraFlyTo,
            _ => FadeThrough,
        };
    }
}

/// <summary>
/// Strongly-typed navigation arrival envelope delivered to <see cref="INavigatedTo{TArgs}"/> when a screen is opened or deep-linked.
/// </summary>
/// <typeparam name="TArgs">Typed route/deep-link argument payload.</typeparam>
/// <param name="Args">Strongly-typed navigation arguments.</param>
/// <param name="FromNeuron">Optional source neuron URI or alias initiating the navigation.</param>
/// <param name="Transition">Requested <see cref="ViewTransition"/> token (defaults to <see cref="ViewTransition.FadeThrough"/>).</param>
/// <param name="CorrelationId">Optional trace or workflow correlation identifier.</param>
public sealed record NavigationArrival<TArgs>(
    TArgs Args,
    string? FromNeuron = null,
    string Transition = ViewTransition.FadeThrough,
    string? CorrelationId = null);

/// <summary>
/// Navigation departure envelope evaluated against <see cref="INavigatedFrom.CanNavigateFrom"/> before leaving a screen.
/// </summary>
/// <param name="ToNeuron">Optional destination neuron URI or alias.</param>
/// <param name="Route">Optional destination route or lens identifier.</param>
/// <param name="Force">When true, bypasses soft <see cref="INavigatedFrom.CanNavigateFrom"/> refusal guards.</param>
public sealed record NavigationDeparture(
    string? ToNeuron,
    string? Route = null,
    bool Force = false);

/// <summary>
/// DDD UI Kit lifecycle contract implemented by an <see cref="AggregateScreen{TState}"/> that accepts strongly-typed route arrival parameters.
/// </summary>
/// <typeparam name="TArgs">Typed navigation payload type.</typeparam>
public interface INavigatedTo<TArgs>
{
    /// <summary>Handles arrival on this screen with strongly-typed <paramref name="arrival"/> context.</summary>
    Task OnNavigatedTo(NavigationArrival<TArgs> arrival, CancellationToken cancellationToken = default);
}

/// <summary>
/// DDD UI Kit lifecycle contract implemented by an <see cref="AggregateScreen{TState}"/> that guards departure with a declarative <see cref="Rule"/>.
/// </summary>
public interface INavigatedFrom
{
    /// <summary>Returns the domain <see cref="Rule"/> governing whether the user/agent may leave the current screen state.</summary>
    Rule CanNavigateFrom(NavigationDeparture departure);

    /// <summary>Invoked after departure is permitted (or forced) to persist or clean up transient screen context.</summary>
    Task OnNavigatedFrom(NavigationDeparture departure, CancellationToken cancellationToken = default);
}

/// <summary>
/// Built-in wire signal routed to <see cref="AggregateScreenBase{TState}"/> to trigger <see cref="INavigatedTo{TArgs}"/> over JSON.
/// </summary>
[Signal(SignalAlias)]
public sealed record UiNavigateTo(
    string? ArgsJson = null,
    string? FromNeuron = null,
    string Transition = ViewTransition.FadeThrough,
    string? CorrelationId = null) : Signal
{
    /// <summary>Frozen signal alias routed to <see cref="AggregateScreenBase{TState}"/>.</summary>
    public const string SignalAlias = "ui.navigate-to";
}

/// <summary>
/// Built-in wire signal routed to <see cref="AggregateScreenBase{TState}"/> to evaluate <see cref="INavigatedFrom"/> guards and depart cleanly.
/// </summary>
[Signal(SignalAlias)]
public sealed record UiNavigateFrom(
    string? ToNeuron = null,
    string? Route = null,
    bool Force = false) : Signal
{
    /// <summary>Frozen signal alias routed to <see cref="AggregateScreenBase{TState}"/>.</summary>
    public const string SignalAlias = "ui.navigate-from";
}

/// <summary>
/// Standard route deep-link payload for built-in browser operator screens (such as <c>AspireApp</c> and <c>OrleansApp</c>).
/// </summary>
/// <param name="Section">Target dashboard tab/section (e.g. <c>resources</c>, <c>traces</c>, <c>grains</c>, <c>silos</c>).</param>
/// <param name="TraceId">Optional distributed trace ID for deep-linking directly to trace details.</param>
/// <param name="GrainFilter">Optional Orleans grain type or activation filter.</param>
/// <param name="Route">Optional explicit sub-route path or hash fragment.</param>
/// <param name="BaseUrl">Optional dashboard endpoint override.</param>
public sealed record BrowserNavArgs(
    string? Section = null,
    string? TraceId = null,
    string? GrainFilter = null,
    string? Route = null,
    string? BaseUrl = null);

/// <summary>
/// Standard route deep-link payload for Studio surfaces (<c>EngineerApp</c> and <c>CreatorApp</c>).
/// </summary>
/// <param name="TargetId">Optional target app, neuron, or 3D graph node identifier to focus.</param>
/// <param name="Lens">Optional active stage lens (e.g. <c>flow</c>, <c>changes</c>, <c>brain</c>, <c>state</c>).</param>
/// <param name="FlowId">Optional active engineering mission/flow identifier.</param>
/// <param name="CandidateId">Optional worktree candidate identifier (e.g. <c>w1</c>, <c>w2</c>).</param>
/// <param name="FilePath">Optional changed file path to select in the diff editor.</param>
/// <param name="Mode">Optional studio mode hint.</param>
public sealed record StudioNavArgs(
    string? TargetId = null,
    string? Lens = null,
    string? FlowId = null,
    string? CandidateId = null,
    string? FilePath = null,
    string? Mode = null);
