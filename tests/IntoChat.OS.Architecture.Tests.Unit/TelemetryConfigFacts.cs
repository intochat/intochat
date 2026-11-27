using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using IntoChat.OS.Host.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Shipped settings never turn on sensitive AI telemetry (prompts and tool output leave the process); only a
// Development settings file opts in. The brain log is written Framed by every shipped host.
public sealed class TelemetryConfigFacts
{
    [Fact]
    public void NoShippedAppsettingsEnablesSensitiveTelemetry()
    {
        var offenders = Shipped().Where(file => Values(file, "EnableSensitiveData").Any(IsTrue)).Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "Telemetry:EnableSensitiveData belongs in appsettings.Development.json only: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAppHostOptsInToSensitiveTelemetryInDevelopmentOnly()
    {
        var development = Directory.EnumerateFiles(Repository.ProjectNamed("IntoChat.OS.AppHost").Directory, "appsettings.Development.json").Single();
        Assert.Contains(Values(development, "EnableSensitiveData"), IsTrue);
    }

    // The runtime BrainLog writes one format since 06-02 (R4-09); no shipped setting picks an os BrainLog format any more.
    [Fact]
    public void NoShippedSettingPicksABrainLogWriteFormat()
    {
        foreach (var name in new[] { "IntoChat.OS.Kernel", "IntoChat.OS.AppHost" })
        {
            var settings = Path.Combine(Repository.ProjectNamed(name).Directory, "appsettings.json");
            Assert.Empty(Values(settings, "WriteFormat"));
        }
    }

    [Fact]
    public void ServiceDefaultsExportsComponentAndOsTelemetrySourcesAndMeters()
    {
        var recordedActivities = new List<string>();
        var recordedMetrics = new List<string>();

        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "TelemetryConfigFactsHost" });
        builder.AddServiceDefaults();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new RecordingActivityProcessor(recordedActivities)))
            .WithMetrics(metrics => metrics.AddReader(new BaseExportingMetricReader(new RecordingMetricExporter(recordedMetrics))));

        using var host = builder.Build();
        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        using (var osSource = new ActivitySource("IntoChat.OS.Modules.TelemetryProbe"))
        using (var componentSource = new ActivitySource("IntoChat.SampleComponent"))
        using (var componentSubSource = new ActivitySource("IntoChat.SampleComponent.Sub"))
        using (var signalSource = new ActivitySource("DigitalBrain.Signals"))
        using (var unrelatedSource = new ActivitySource("Unrelated.External"))
        {
            using (osSource.StartActivity("os-op")) { }
            using (componentSource.StartActivity("component-op")) { }
            using (componentSubSource.StartActivity("component-sub-op")) { }
            using (signalSource.StartActivity("signal.publish")) { }
            using (signalSource.StartActivity("signal.react")) { }
            using (unrelatedSource.StartActivity("unrelated-op")) { }
        }

        using (var osMeter = new Meter("IntoChat.OS.Modules.TelemetryProbe"))
        using (var componentMeter = new Meter("IntoChat.SampleComponent"))
        using (var unrelatedMeter = new Meter("Unrelated.External"))
        {
            osMeter.CreateCounter<long>("os.probe.count").Add(1);
            componentMeter.CreateCounter<long>("component.probe.count").Add(1);
            unrelatedMeter.CreateCounter<long>("unrelated.probe.count").Add(1);
            meterProvider.ForceFlush();
        }

        tracerProvider.ForceFlush();

        Assert.Contains("IntoChat.OS.Modules.TelemetryProbe:os-op", recordedActivities);
        Assert.Contains("IntoChat.SampleComponent:component-op", recordedActivities);
        Assert.Contains("IntoChat.SampleComponent.Sub:component-sub-op", recordedActivities);
        Assert.Contains("DigitalBrain.Signals:signal.publish", recordedActivities);
        Assert.Contains("DigitalBrain.Signals:signal.react", recordedActivities);
        Assert.DoesNotContain(recordedActivities, entry => entry.StartsWith("Unrelated.External:", StringComparison.Ordinal));

        Assert.Contains("IntoChat.OS.Modules.TelemetryProbe:os.probe.count", recordedMetrics);
        Assert.Contains("IntoChat.SampleComponent:component.probe.count", recordedMetrics);
        Assert.DoesNotContain(recordedMetrics, entry => entry.StartsWith("Unrelated.External:", StringComparison.Ordinal));
    }

    private sealed class RecordingActivityProcessor(List<string> recorded) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (recorded)
            {
                recorded.Add($"{data.Source.Name}:{data.OperationName}");
            }
        }
    }

    private sealed class RecordingMetricExporter(List<string> recorded) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            lock (recorded)
            {
                foreach (var metric in batch)
                {
                    recorded.Add($"{metric.MeterName}:{metric.Name}");
                }
            }
            return ExportResult.Success;
        }
    }

    private static IEnumerable<string> Shipped()
        => Repository.ProjectFiles("appsettings*.json", path => !path.EndsWith(".Development.json", StringComparison.Ordinal));

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Every value of the named property anywhere in the document, nested or as the tail of a flat key.
    private static IEnumerable<string> Values(string file, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var found = new List<string>();
        Walk(document.RootElement);
        return found;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name == name || property.Name.EndsWith(":" + name, StringComparison.Ordinal))
                        {
                            found.Add(property.Value.ToString());
                        }
                        Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) { Walk(item); }
                    break;
            }
        }
    }
}
