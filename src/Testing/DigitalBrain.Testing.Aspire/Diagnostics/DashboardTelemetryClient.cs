using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalBrain.Testing.Aspire.Diagnostics;

/// <summary>Queries the Aspire dashboard's retained OTLP JSON telemetry.</summary>
internal sealed class DashboardTelemetryClient(HttpClient http)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public Task<DashboardTelemetryData> GetSpansAsync(string? traceId, CancellationToken cancellationToken)
        => QueryAsync("spans", traceId, cancellationToken);

    public Task<DashboardTelemetryData> GetLogsAsync(string? traceId, CancellationToken cancellationToken)
        => QueryAsync("logs", traceId, cancellationToken);

    private async Task<DashboardTelemetryData> QueryAsync(string signal, string? traceId, CancellationToken cancellationToken)
    {
        // The 13.6 dashboard has no paging cursor or offset. Request the entire matching retained set.
        var path = $"api/telemetry/{signal}?limit={int.MaxValue}";
        if (traceId is not null)
        {
            path += "&traceId=" + Uri.EscapeDataString(traceId);
        }

        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<Response>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The dashboard returned an empty telemetry response.");
        if (envelope.Data is null || envelope.TotalCount is null || envelope.ReturnedCount is null
            || envelope.TotalCount < 0 || envelope.ReturnedCount < 0)
        {
            throw new InvalidDataException("The dashboard returned an incomplete telemetry response envelope.");
        }

        if (envelope.TotalCount != envelope.ReturnedCount)
        {
            throw new InvalidDataException($"The dashboard truncated {signal}: returned {envelope.ReturnedCount} of {envelope.TotalCount} records. Assertions cannot use an incomplete snapshot.");
        }

        int actualCount;
        try
        {
            actualCount = signal == "spans"
                ? envelope.Data.ResourceSpans.Sum(resource => resource.ScopeSpans.Sum(scope => scope.Spans.Length))
                : envelope.Data.ResourceLogs.Sum(resource => resource.ScopeLogs.Sum(scope => scope.LogRecords.Length));
        }
        catch (Exception exception) when (exception is NullReferenceException or ArgumentNullException)
        {
            throw new InvalidDataException("The dashboard returned a malformed telemetry collection.", exception);
        }

        if (actualCount != envelope.ReturnedCount)
        {
            throw new InvalidDataException($"The dashboard reported {envelope.ReturnedCount} {signal} but supplied {actualCount} records.");
        }

        return envelope.Data;
    }

    private sealed class Response
    {
        public Response() { }

        public DashboardTelemetryData? Data { get; init; }
        public int? TotalCount { get; init; }
        public int? ReturnedCount { get; init; }
    }
}
