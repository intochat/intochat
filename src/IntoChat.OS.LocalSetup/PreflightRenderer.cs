using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.LocalSetup;

internal sealed record PreflightJsonResult(string Id, string Status, string Message, string? Fix);

internal sealed record PreflightJsonReport(int ExitCode, bool GpuDetected, PreflightJsonResult[] Results);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PreflightJsonReport))]
internal sealed partial class PreflightReportJson : JsonSerializerContext;

// Writes check ids, statuses, messages and fix lines only; never environment values or tokens.
public static class PreflightRenderer
{
    public static void Render(PreflightReport report, int exitCode, bool json, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        if (json)
        {
            var document = new PreflightJsonReport(
                exitCode,
                report.GpuDetected,
                [.. report.Results.Select(static result => new PreflightJsonResult(
                    result.Id,
                    result.Status.ToString().ToUpperInvariant(),
                    result.Message,
                    result.Fix))]);
            output.WriteLine(JsonSerializer.Serialize(document, PreflightReportJson.Default.PreflightJsonReport));
            return;
        }
        foreach (var result in report.Results)
        {
            output.WriteLine($"[{result.Status.ToString().ToUpperInvariant()}] {result.Id}: {result.Message}");
            if (result.Fix is not null && result.Status != CheckStatus.Pass) { output.WriteLine($"  fix: {result.Fix}"); }
        }
    }
}
