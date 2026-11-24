/*
# Usage
Role: Default
Shipped: true
Operations: open, refresh, set-budget

Shows this brain's recorded compute spending over the last 30 days in USD, converted from micro-credits.
Reads through the scope-checked compute neuron when available and falls back to default telemetry in standalone mode.
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Logging;
using static IntoChat.OS.UI.Kit;

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
    public UsageAppState() : this(0, "$0.00", "No compute recorded.", [], ComputeState.DefaultMonthlyBudgetMicros, false) { }

    [Invariant("Recorded spend in micro-credits is non-negative."), JsonIgnore]
    public bool NonNegativeSpend => SpentMicros >= 0;

    [Invariant("Monthly compute budget cap is positive."), JsonIgnore]
    public bool PositiveMonthlyCap => MonthlyBudgetMicros > 0;

    [Invariant("BudgetExceeded flag is consistent with SpentMicros and MonthlyBudgetMicros."), JsonIgnore]
    public bool ConsistentBudgetFlag => BudgetExceeded == (SpentMicros > MonthlyBudgetMicros);

    public Rule ValidBudgetCap(long budgetMicros) =>
        Decision.Rule("non_positive_budget", budgetMicros, static b => b <= 0, "Monthly compute cap must be greater than $0.00.");

    public Decision<UsageAppState> ApplyBudgetCap([Sample(10_000_000L, 50_000_000L, 200_000_000L, 0L)] long monthlyBudgetMicros)
    {
        if (Decision.Check("set-budget", ValidBudgetCap(monthlyBudgetMicros)) is { } refusal) { return refusal; }
        var usd = UsageApp.FormatUsd(SpentMicros);
        return Decision.Accept(this with
        {
            FormattedUsd = usd,
            MonthlyBudgetMicros = monthlyBudgetMicros,
            BudgetExceeded = SpentMicros > monthlyBudgetMicros,
            Status = $"Updated monthly cap to {UsageApp.FormatUsd(monthlyBudgetMicros)}. Total: {usd}.",
        });
    }

    [IgnoreExplore]
    public UsageAppState ApplySnapshot(ComputeState compute, string status)
    {
        var spent = Math.Max(0L, compute.SpentMicros);
        var cap = compute.MonthlyBudgetMicros > 0 ? compute.MonthlyBudgetMicros : ComputeState.DefaultMonthlyBudgetMicros;
        return new(spent, UsageApp.FormatUsd(spent), status, compute.Rows ?? [], cap, spent > cap);
    }
}

[Neuron("app.usage"), Screen("app.usage", Title = "Usage", Icon = "wallet", Category = "System")]
public sealed partial class UsageApp : AggregateScreen<UsageAppState>
{
    public const string UsageSpendChartKey = "usage/charts/spend";

    [Slot(Key = "compute", Required = false)] private partial ICompute? Compute { get; }
    [Slot(Key = UsageSpendChartKey, Required = false)] private partial IntoChat.OS.Modules.UI.Chart.IChart? SpendChart { get; }
    public static AppBundle Bundle => AppBundle.FromScreen<UsageApp>(
        "usage",
        "1.0.0",
        description: "This brain's compute spending over the last 30 days.",
        operations:
        [
            new("open", "Read current usage."),
            new("refresh", "Refresh recorded usage."),
            new("set-budget", "Update this brain's 30-day monthly compute spend cap in micro-credits."),
        ]);

    public new UsageAppState State => base.State;
    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        try { await Follow<ICompute, ComputeChanged>("compute").ConfigureAwait(true); }
        catch (Exception ex) { OsConsoleLog.For<UsageApp>().LogSwallowed(ex, "Compute follow is optional when running in standalone mode."); }
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
    public Task SetBudget(UsageSetBudget request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return UpdateBudgetCapAsync(request.MonthlyBudgetMicros > 0 ? request.MonthlyBudgetMicros : ComputeState.DefaultMonthlyBudgetMicros);
    }

    public Task RefreshUsageAsync() => ReadComputeAsync("Refreshed compute spending.");

    public async Task UpdateBudgetCapAsync(long cap)
    {
        if (State.ValidBudgetCap(cap).Broken) { await ApplyDecisionAsync(State.ApplyBudgetCap(cap)); return; }
        if (Compute is not { } computeSlot)
        {
            await ApplyDecisionAsync(State.ApplyBudgetCap(cap));
            return;
        }
        try
        {
            var compute = await computeSlot.SetBudget(cap);
            await Show(State.ApplySnapshot(compute, $"Updated monthly cap to {FormatUsd(compute.MonthlyBudgetMicros)}. Total: {FormatUsd(compute.SpentMicros)} / {FormatUsd(compute.MonthlyBudgetMicros)}."));
        }
        catch (Exception ex)
        {
            OsConsoleLog.For<UsageApp>().LogSwallowed(ex, "Failed to set compute budget; falling back to local state.");
            await ApplyDecisionAsync(State.ApplyBudgetCap(cap));
        }
    }

    private async Task ReadComputeAsync(string statusPrefix)
    {
        if (Compute is not { } computeSlot)
        {
            var usd = FormatUsd(State.SpentMicros);
            await Show(State with { FormattedUsd = usd, Status = $"{statusPrefix} Total: {usd}." });
            return;
        }
        try
        {
            var compute = await computeSlot.Read();
            var rows = compute.Rows ?? [];
            if (SpendChart is { } chart)
            {
                try
                {
                    await chart.Render(new IntoChat.OS.Modules.UI.Chart.ChartSpec(
                        Title: "Spend by Surface (USD)",
                        Kind: IntoChat.OS.Modules.UI.Chart.ChartKinds.Columns,
                        Series:
                        [
                            new IntoChat.OS.Modules.UI.Chart.ChartSeries("Spend",
                            [
                                .. rows.Select(r => new IntoChat.OS.Modules.UI.Chart.ChartPoint(r.Key, Math.Round(r.Micros / 1_000_000d, 2))),
                            ]),
                        ],
                        Description: "Live metered compute spend per surface (last 30 days).",
                        Prefix: "$",
                        From: "Compute"));
                }
                catch (Exception ex) { OsConsoleLog.For<UsageApp>().LogSwallowed(ex, "Usage IChart sync skipped in minimal test host."); }
            }
            await Show(State.ApplySnapshot(compute, $"{statusPrefix} Total: {FormatUsd(compute.SpentMicros)} / {FormatUsd(compute.MonthlyBudgetMicros)} across {rows.Length} surface(s)."));
        }
        catch (Exception ex)
        {
            OsConsoleLog.For<UsageApp>().LogSwallowed(ex, "Failed to read compute state; falling back to local state.");
            var usd = FormatUsd(State.SpentMicros);
            await Show(State with { FormattedUsd = usd, Status = $"{statusPrefix} Total: {usd}." });
        }
    }

    public static string FormatUsd(long micros) => string.Create(CultureInfo.InvariantCulture, $"${micros / 1_000_000m:0.00}");

    protected override Node Render(UsageAppState state)
    {
        var rows = state.Rows ?? [];
        var budgetUsd = FormatUsd(state.MonthlyBudgetMicros);
        var spendTone = state.BudgetExceeded ? BadgeTone.Error : BadgeTone.Accent;

        return VStack(Space.Md)[
            HeaderBar(
                "Usage",
                $"Amounts in USD · 1,000,000 micro-credits = $1.00 · Monthly Cap: {budgetUsd}",
                StatusPill(state.BudgetExceeded ? StatusKind.Error : StatusKind.Done, state.BudgetExceeded ? "Over Cap" : "Within Cap"),
                PrimaryButton("Refresh usage", Icons.RefreshCw).Key("refresh-usage").Small().OnClick(() => RefreshUsageAsync())),
            Grid(3, Space.Sm)[
                StatCard("kpi-usage-usd", "30-Day Spend", state.FormattedUsd, $"{state.SpentMicros:N0} µ-credits (Cap {budgetUsd})", spendTone, "dollar-sign"),
                StatCard("kpi-usage-surfaces", "Active Surfaces", $"{rows.Length}", "Metered Neurons", BadgeTone.Done, "cpu"),
                StatCard("kpi-usage-calls", "Recorded Calls", $"{rows.Sum(static r => r.Count)}", "Last 30 Days", BadgeTone.Neutral, "activity")
            ],
            Card("Monthly Spend Cap", state.Status)[
                HStack(Space.Sm)[
                    Button("Cap $10", "gauge").Key("cap-10").Small().OnClick(() => UpdateBudgetCapAsync(10_000_000L)),
                    Button("Cap $50", "gauge").Key("cap-50").Small().OnClick(() => UpdateBudgetCapAsync(50_000_000L)),
                    Button("Cap $200", "gauge").Key("cap-200").Small().OnClick(() => UpdateBudgetCapAsync(200_000_000L))
                ]
            ],
            rows.Length > 0
                ? new NeuronRef("ui.chart", UsageSpendChartKey)
                : null,
            Table(
                "Metered Compute by Surface",
                ["Surface", "Calls", "Micro-Credits", "USD"],
                rows.Select(r => (IReadOnlyList<string>)[r.Key, r.Count.ToString(CultureInfo.InvariantCulture), r.Micros.ToString("N0", CultureInfo.InvariantCulture), FormatUsd(r.Micros)]).ToArray())
        ];
    }
}
