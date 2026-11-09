/*
# Usage
Role: Default
Shipped: true
Operations: open, refresh

Shows this brain's recorded compute spending over the last 30 days in USD, converted from micro-credits.
Reads through the scope-checked compute neuron when available and falls back to default telemetry in standalone mode.
*/
using System.Globalization;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Compute;

namespace IntoChat.OS.Apps.Usage;

[DigitalBrain.Signal("usage.open")]
public sealed record UsageOpen : DigitalBrain.Signal;

[DigitalBrain.Signal("usage.refresh")]
public sealed record UsageRefresh : DigitalBrain.Signal;

public sealed record UsageAppState(
    long SpentMicros,
    string FormattedUsd,
    string Status,
    ComputeRow[] Rows)
{
    public UsageAppState() : this(
        SpentMicros: 0,
        FormattedUsd: "$0.00",
        Status: "No compute recorded.",
        Rows: [])
    {
    }
}

[DigitalBrain.App("usage", Accent = "#38BDF8", Category = "System")]
public sealed class UsageApp : DigitalBrain.App<UsageAppState>
{
    public static DigitalBrain.AppDefinition Definition { get; } = new(typeof(UsageApp));

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
            ],
            Settings: [],
            Slots: [],
            Icon: "icon.svg"),
        "",
        new Dictionary<string, string>(StringComparer.Ordinal))
    {
        CompiledType = typeof(UsageApp).AssemblyQualifiedName,
    };

    [DigitalBrain.On]
    public Task Open(UsageOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadComputeAsync("Recorded compute spending over the last 30 days.");
    }

    [DigitalBrain.On]
    public Task Refresh(UsageRefresh request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadComputeAsync("Refreshed compute spending.");
    }

    private async Task ReadComputeAsync(string statusPrefix)
    {
        try
        {
            var compute = await Get<ICompute>("compute").Read();
            var rows = compute.Rows ?? [];
            var usd = FormatUsd(compute.SpentMicros);
            Set(new UsageAppState(
                SpentMicros: compute.SpentMicros,
                FormattedUsd: usd,
                Status: $"{statusPrefix} Total: {usd} across {rows.Length} surface(s).",
                Rows: rows));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Swallow(error, "Compute neuron is optional when UsageApp runs without Flutter module registered.");
            var usd = FormatUsd(State.SpentMicros);
            Set(State with
            {
                FormattedUsd = usd,
                Status = $"{statusPrefix} Total: {usd}.",
            });
        }
    }

    public static string FormatUsd(long micros)
    {
        var dollars = micros / 1_000_000m;
        return string.Create(CultureInfo.InvariantCulture, $"${dollars:0.00}");
    }

    public View Render()
    {
        var rows = State.Rows ?? [];
        var totalCalls = rows.Sum(static r => r.Count);

        return new(
            "Usage",
            new Column(
            [
                new Grid(
                    3,
                    [
                        Ui.StatCard("kpi-usage-usd", "30-Day Spend", State.FormattedUsd, $"{State.SpentMicros:N0} µ-credits", BadgeTone.Accent, "dollar-sign"),
                        Ui.StatCard("kpi-usage-surfaces", "Active Surfaces", $"{rows.Length}", "Metered Neurons", BadgeTone.Done, "cpu"),
                        Ui.StatCard("kpi-usage-calls", "Recorded Calls", $"{totalCalls}", "Last 30 Days", BadgeTone.Neutral, "activity"),
                    ],
                    Gap: 10),
                new Surface(
                    [
                        new Text(State.Status),
                        new Row(
                        [
                            new Button("refresh-usage", "Refresh usage", Emit: AppSignals.Emit("usage", "refresh"), Tone: ButtonTone.Accent)
                            {
                                Icon = "refresh-cw",
                                Small = true,
                            },
                        ]) { Gap = 8 },
                    ],
                    Kind: SurfaceKind.Elevated,
                    Title: "Usage",
                    Subtitle: "Amounts in USD · 1,000,000 micro-credits = $1."),
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

    [DigitalBrain.Scenario("Usage reads and refreshes 30-day compute spending in USD")]
    public async Task VerifyUsageComputeSummaryScenario()
    {
        await Open(new UsageOpen());
        await Refresh(new UsageRefresh());
        if (State.SpentMicros < 0 || !State.FormattedUsd.StartsWith('$'))
        {
            throw new InvalidOperationException("Expected UsageApp to report positive 30-day compute spending in USD.");
        }
    }
}
