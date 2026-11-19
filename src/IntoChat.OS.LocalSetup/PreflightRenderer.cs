using System.Text.Json;

namespace IntoChat.OS.LocalSetup;

// Writes check ids, statuses, messages and fix lines only; never environment values or tokens.
public static class PreflightRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Render(PreflightReport report, int exitCode, bool json, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);
        if (json)
        {
            var document = new
            {
                exitCode,
                gpuDetected = report.GpuDetected,
                results = report.Results.Select(result => new { id = result.Id, status = result.Status.ToString().ToUpperInvariant(), message = result.Message, fix = result.Fix }),
            };
            output.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
            return;
        }
        foreach (var result in report.Results)
        {
            output.WriteLine($"[{result.Status.ToString().ToUpperInvariant()}] {result.Id}: {result.Message}");
            if (result.Fix is not null && result.Status != CheckStatus.Pass) { output.WriteLine($"  fix: {result.Fix}"); }
        }
    }
}
