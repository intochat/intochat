/*
# Trace
Role: Default
Shipped: true
Operations: open, play, pause, back, forward, live

Follows one operation across neurons: the trace's journaled signals folded into a causation tree, a graph of the publishers with
the selected hop's link lit, and a replay bar. Stepping is manual: Play follows live, Pause stops following, Back and Forward step.
*/
using DigitalBrain;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Apps.Trace;

[DigitalBrain.Signal("trace.open")]
public sealed record TraceOpen(string? Input = null) : DigitalBrain.Signal;

[DigitalBrain.Signal("trace.play")]
public sealed record TracePlay : DigitalBrain.Signal;

[DigitalBrain.Signal("trace.pause")]
public sealed record TracePause : DigitalBrain.Signal;

[DigitalBrain.Signal("trace.back")]
public sealed record TraceBack : DigitalBrain.Signal;

[DigitalBrain.Signal("trace.forward")]
public sealed record TraceForward : DigitalBrain.Signal;

[DigitalBrain.Signal("trace.live")]
public sealed record TraceLive : DigitalBrain.Signal;

[DigitalBrain.App("trace", Accent = "#06B6D4", Category = "Developer")]
public sealed class TraceApp : DigitalBrain.App<TraceState>
{
    public const int MaxHops = 5000;

    // Render cannot await: the last read of the open trace, refreshed on open and on Play or Live.
    private TraceHop[] _hops = [];
    private bool _untraced = true;

    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(TraceApp));

    public static AppBundle Bundle { get; } = new(
        "trace",
        "1.0.0",
        new AppManifest(
            Title: "Trace",
            Description: "Follow one operation across neurons: causation tree, lit graph and replay bar.",
            Operations:
            [
                new("open", "Open a trace (input: trace/{traceId})."),
                new("play", "Follow the trace live."),
                new("pause", "Stop following; keep the selected hop."),
                new("back", "Step to the previous hop."),
                new("forward", "Step to the next hop."),
                new("live", "Follow the latest hop."),
            ],
            Settings: [],
            Slots: [],
            Icon: "activity",
            Toolbar: []),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(TraceApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public Task Open(TraceOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = request.Input?.Trim() ?? "";
        var traceId = input.StartsWith("trace/", StringComparison.Ordinal) ? input["trace/".Length..] : input;
        Set(State with { TraceId = string.IsNullOrWhiteSpace(traceId) ? State.TraceId : traceId, Since = 0, Live = true, SelectedSequence = null });
        return RefreshAsync();
    }

    // No timers in apps: Play follows live.
    [DigitalBrain.On]
    public Task Play(TracePlay request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Live(new TraceLive());
    }

    [DigitalBrain.On]
    public void Pause(TracePause request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { Live = false, SelectedSequence = TraceViewRenderer.Selected(State, _hops) });
    }

    [DigitalBrain.On]
    public void Back(TraceBack request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { Live = false, SelectedSequence = TraceViewRenderer.Step(State, _hops, -1) });
    }

    [DigitalBrain.On]
    public void Forward(TraceForward request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { Live = false, SelectedSequence = TraceViewRenderer.Step(State, _hops, +1) });
    }

    [DigitalBrain.On]
    public Task Live(TraceLive request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Set(State with { Live = true });
        return RefreshAsync();
    }

    public View Render() => TraceViewRenderer.Render(State, _hops, _untraced);

    private async Task RefreshAsync()
    {
        var signals = new List<TraceEntry>();
        if (State.TraceId is { } traceId)
        {
            try
            {
                var since = State.Since;
                while (signals.Count < MaxHops)
                {
                    var page = await Get<ITraceReader>(ITraceReader.DefaultKey).ReadTrace(traceId, since, SignalPage.MaxLimit);
                    signals.AddRange(page.Entries);
                    if (page.Entries.Length < SignalPage.MaxLimit) { break; }
                    since = page.Entries[^1].Sequence;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Swallow(error, "The trace reader is optional; the view shows what was read.");
            }
        }
        var held = signals.Take(MaxHops).ToArray();
        _untraced = held.Length == 0 || held.All(s => s.TraceId is null);
        _hops = TraceViewRenderer.Fold(held);
        Set(State);
    }
}
