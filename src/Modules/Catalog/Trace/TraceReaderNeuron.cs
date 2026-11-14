using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Signals;

namespace IntoChat.OS.Modules.Apps.Trace;

// Forwards to the brain's signal history. Payloads are shown as journaled, with inline tokens, PATs, URL credentials, and passwords redacted.
[Neuron("trace.reader"), StatelessNeuron]
public sealed partial class TraceReaderNeuron(ISignalHistory history) : Neuron, ITraceReader
{
    public const int PayloadLimit = 2000;

    public async Task<TracePage> ReadTrace(string traceId, long since, int limit)
    {
        var page = await history.ReadTrace(traceId, since, limit, CancellationToken.None);
        return new TracePage([.. page.Signals.Select(Entry)]);
    }

    // Cuts at PayloadLimit UTF-16 units without splitting a surrogate pair.
    public static string Trim(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length <= PayloadLimit) { return payload; }
        var cut = char.IsHighSurrogate(payload[PayloadLimit - 1]) ? PayloadLimit - 1 : PayloadLimit;
        return payload[..cut] + "…";
    }

    public static string Redact(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var step1 = UrlCredentialsPattern().Replace(payload, "$1[REDACTED]@");
        var step2 = SecretTokenPattern().Replace(step1, "[REDACTED]");
        return ConnectionSecretPattern().Replace(step2, "$1$2[REDACTED]");
    }

    public static TraceEntry Entry(JournaledSignal journaled)
    {
        ArgumentNullException.ThrowIfNull(journaled);
        var type = journaled.Signal.GetType();
        string payload;
        try { payload = JsonSerializer.Serialize(journaled.Signal, type, JsonSerializerOptions.Web); }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { payload = type.Name; }
        payload = Trim(Redact(payload));
        return new TraceEntry(journaled.Publisher.ToString(), journaled.Sequence, type.GetCustomAttribute<SignalAttribute>()?.Alias ?? type.Name,
            journaled.At, journaled.TraceId, journaled.CausationId, payload);
    }

    [GeneratedRegex(@"(?:Bearer\s+[A-Za-z0-9._\-/+]+=*|sk-(?:proj-|ant-(?:api\d{2}-)?)?[A-Za-z0-9_\-]{8,}|github_pat_[A-Za-z0-9_]{12,}|gh[pousr]_[A-Za-z0-9_]{8,}|AIza[0-9A-Za-z_\-]{20,}|xox[baprs]-[0-9A-Za-z\-]{10,}|vault://[A-Za-z0-9._\-/:]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretTokenPattern();

    [GeneratedRegex(@"(https?://)[^/\s:@""']+:[^/\s@""']+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialsPattern();

    [GeneratedRegex(@"(?i)\b(password|pwd|client_secret|access_token|refresh_token|api_key|apikey)\s*([=:])\s*([^\s;,""'\}]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionSecretPattern();
}
