/*
# Usage
Role: Default
Shipped: true
Operations: open, refresh, set-budget

Shows this brain's recorded compute spending over the last 30 days in USD, converted from micro-credits.
Reads through the scope-checked compute neuron when available and falls back to default telemetry in standalone mode.
*/
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Compute;
using DigitalBrain.Log;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Apps.Usage;

[Signal("usage.open")]
public sealed record UsageOpen : Signal;

[Signal("usage.refresh")]
public sealed record UsageRefresh : Signal;

[Signal("usage.set-budget")]
public sealed record UsageSetBudget(long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros) : Signal;

public sealed record UsageAppState(
    long SpentMicros,
    string FormattedUsd,
    string Status,
    ComputeRow[] Rows,
    long MonthlyBudgetMicros = ComputeState.DefaultMonthlyBudgetMicros,
    bool BudgetExceeded = false)
{
    public UsageAppState() : this(
        SpentMicros: 0,
        FormattedUsd: "$0.00",
        Status: "No compute recorded.",
        Rows: [],
        MonthlyBudgetMicros: ComputeState.DefaultMonthlyBudgetMicros,
        BudgetExceeded: false)
    {
    }
}

[Neuron("app.usage")]
public sealed class UsageApp : Screen<UsageAppState>
{
    public static AppBundle Bundle { get; } = new(
        "usage",
        "1.0.0",
        new AppManifest(
            Title: "Usage",
            Description: "This brain's compute spending over the last 30 days.",
            Operations:
            [
                new("open", "Read current usage."),
                new("refresh", "Refresh recorded usage."),
                new("set-budget", "Update this brain's 30-day monthly compute spend cap in micro-credits."),
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(UsageApp).AssemblyQualifiedName,
    };

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        try
        {
            await Follow<ICompute, ComputeChanged>("compute").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "Compute follow is optional when running in standalone mode.");
        }
    }

    [On]
    public Task HandleComputeChanged(ComputeChanged _) => ReadComputeAsync("Live compute update.");

    [On]
    public Task Open(UsageOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadComputeAsync("Recorded compute spending over the last 30 days.");
    }

    [On]
    public Task Refresh(UsageRefresh request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadComputeAsync("Refreshed compute spending.");
    }

    [On]
    public async Task SetBudget(UsageSetBudget request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cap = request.MonthlyBudgetMicros > 0 ? request.MonthlyBudgetMicros : ComputeState.DefaultMonthlyBudgetMicros;
        try
        {
            var compute = await Get<ICompute>("compute").SetBudget(cap);
            await ApplyComputeStateAsync(compute, $"Updated monthly cap to {FormatUsd(compute.MonthlyBudgetMicros)}. Total: {FormatUsd(compute.SpentMicros)} / {FormatUsd(compute.MonthlyBudgetMicros)}.");
        }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "Failed to set compute budget; falling back to local state.");
            var usd = FormatUsd(State.SpentMicros);
            await Show(State with
            {
                MonthlyBudgetMicros = cap,
                BudgetExceeded = State.SpentMicros > cap,
                Status = $"Updated monthly cap to {FormatUsd(cap)}. Total: {usd}.",
            });
        }
    }

    private async Task ReadComputeAsync(string statusPrefix)
    {
        try
        {
            var compute = await Get<ICompute>("compute").Read();
            var rows = compute.Rows ?? [];
            await ApplyComputeStateAsync(compute, $"{statusPrefix} Total: {FormatUsd(compute.SpentMicros)} / {FormatUsd(compute.MonthlyBudgetMicros)} across {rows.Length} surface(s).");
        }
        catch (Exception ex)
        {
            NullLogger.Instance.LogSwallowed(ex, "Failed to read compute state; falling back to local state.");
            var usd = FormatUsd(State.SpentMicros);
            await Show(State with { FormattedUsd = usd, Status = $"{statusPrefix} Total: {usd}." });
        }
    }

    private Task ApplyComputeStateAsync(ComputeState compute, string status) =>
        Show(new UsageAppState(
            SpentMicros: compute.SpentMicros,
            FormattedUsd: FormatUsd(compute.SpentMicros),
            Status: status,
            Rows: compute.Rows ?? [],
            MonthlyBudgetMicros: compute.MonthlyBudgetMicros,
            BudgetExceeded: compute.BudgetExceeded));

    public static string FormatUsd(long micros) =>
        string.Create(CultureInfo.InvariantCulture, $"${micros / 1_000_000m:0.00}");

    public new UsageAppState State => base.State;
    public View Render() => Render(State);

    private static new Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        var alias = typeof(TSignal).GetCustomAttribute<SignalAttribute>(false)?.Alias ?? typeof(TSignal).Name;
        return new Emit("app.usage", alias, JsonSerializer.SerializeToElement(signal));
    }
    protected override View Render(UsageAppState state)
    {
        var rows = state.Rows ?? [];
        var totalCalls = rows.Sum(static r => r.Count);
        var budgetUsd = FormatUsd(state.MonthlyBudgetMicros);
        var spendTone = state.BudgetExceeded ? BadgeTone.Error : BadgeTone.Accent;

        return new(
            "Usage",
            new Column(
            [
                new Grid(
                    3,
                    [
                        Ui.StatCard("kpi-usage-usd", "30-Day Spend", state.FormattedUsd, $"{state.SpentMicros:N0} µ-credits (Cap {budgetUsd})", spendTone, "dollar-sign"),
                        Ui.StatCard("kpi-usage-surfaces", "Active Surfaces", $"{rows.Length}", "Metered Neurons", BadgeTone.Done, "cpu"),
                        Ui.StatCard("kpi-usage-calls", "Recorded Calls", $"{totalCalls}", "Last 30 Days", BadgeTone.Neutral, "activity"),
                    ],
                    Gap: 10),
                new Surface(
                    [
                        new Text(state.Status),
                        new Row(
                        [
                            new Button("refresh-usage", "Refresh usage", Emit: Emits(new UsageRefresh()), Tone: ButtonTone.Accent) { Icon = "refresh-cw", Small = true },
                            new Button("cap-10", "Cap $10", Emit: Emits(new UsageSetBudget(10_000_000L))) { Icon = "gauge", Small = true },
                            new Button("cap-50", "Cap $50", Emit: Emits(new UsageSetBudget(50_000_000L))) { Icon = "gauge", Small = true },
                            new Button("cap-200", "Cap $200", Emit: Emits(new UsageSetBudget(200_000_000L))) { Icon = "gauge", Small = true },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Usage",
                    Subtitle: $"Amounts in USD · 1,000,000 micro-credits = $1 · Monthly Cap: {budgetUsd}."),
                new DataTable(
                    Title: "Metered Compute by Surface",
                    Columns: ["Surface", "Calls", "Micro-Credits", "USD"],
                    Rows: rows.Select(r => (IReadOnlyList<string>)
                    [
                        r.Key,
                        r.Count.ToString(CultureInfo.InvariantCulture),
                        r.Micros.ToString("N0", CultureInfo.InvariantCulture),
                        FormatUsd(r.Micros),
                    ]).ToArray()),
            ])
            { Gap = 12 });
    }
}
