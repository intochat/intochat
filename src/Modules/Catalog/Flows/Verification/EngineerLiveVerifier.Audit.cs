using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal sealed partial class EngineerLiveVerifier
{
    private void CaptureStoppedActivity(Activity activity)
    {
        var isError = activity.Status == ActivityStatusCode.Error;
        if (!isError)
        {
            foreach (var tag in activity.TagObjects)
            {
                if ((string.Equals(tag.Key, "otel.status_code", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tag.Value?.ToString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(tag.Key, "error", StringComparison.OrdinalIgnoreCase)
                        && (tag.Value is true || string.Equals(tag.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
                    || (string.Equals(tag.Key, "error.type", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(tag.Value?.ToString())))
                {
                    isError = true;
                    break;
                }
            }
        }

        string? brainTraceId = null;
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, "brain.trace_id", StringComparison.Ordinal))
            {
                brainTraceId = tag.Value?.ToString();
                break;
            }
        }

        var captured = new CapturedSpan(
            activity.Source.Name,
            activity.OperationName,
            activity.TraceId.ToString(),
            brainTraceId,
            isError,
            activity.StatusDescription,
            (long)Math.Round(activity.Duration.TotalMilliseconds));

        lock (_spanGate)
        {
            while (_capturedSpans.Count >= MaxCapturedSpans)
            {
                _capturedSpans.Dequeue();
            }

            _capturedSpans.Enqueue(captured);
        }
    }

    private CapturedSpan[] SnapshotCapturedSpans(string? brainTraceId)
    {
        CapturedSpan[] all;
        lock (_spanGate)
        {
            all = [.. _capturedSpans];
        }

        // The listener sees every span in the process. A run with a trace judges only its own spans: an unrelated brain's
        // failing call (another agent, another test) must not fail this run's live verification. Without a trace there is
        // nothing to scope by, so every captured span counts.
        if (string.IsNullOrWhiteSpace(brainTraceId))
        {
            return all;
        }

        return [.. all.Where(s =>
            string.Equals(s.BrainTraceId, brainTraceId, StringComparison.Ordinal)
            || string.Equals(s.OtelTraceId, brainTraceId, StringComparison.Ordinal))];
    }

    private static bool IsErrorTraceEntry(TraceEntry entry)
    {
        if (entry.Signal.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".rejected", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(entry.Signal, "flow.done", StringComparison.OrdinalIgnoreCase)
            && (entry.Payload.Contains("\"outcome\":\"failed\"", StringComparison.OrdinalIgnoreCase)
                || entry.Payload.Contains("\"outcome\":1", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static bool HasViewRootEnvelope(JsonElement element)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty("root", out _);

    private static ScreenViewAudit AuditScreenViewJson(JsonElement element, bool enforceUiInvariants)
    {
        var violations = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
        {
            violations.Add("View payload is not a JSON object.");
            return new ScreenViewAudit(0, 0, 1, violations);
        }

        var rootTarget = element;
        if (element.TryGetProperty("root", out var rootProp))
        {
            if (rootProp.ValueKind != JsonValueKind.Object)
            {
                violations.Add("View.Root is null or not an object.");
                return new ScreenViewAudit(0, 0, 1, violations);
            }

            rootTarget = rootProp;
        }

        if (enforceUiInvariants)
        {
            if (!TryGetNonBlankString(rootTarget, "kind", out _))
            {
                violations.Add("View.Root is missing a valid node 'kind'.");
            }
        }

        var nodes = 0;
        var keyedCount = 0;
        var errors = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        WalkViewElement(rootTarget, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
        return new ScreenViewAudit(nodes, keyedCount, errors, violations);
    }

    private static void WalkViewElement(
        JsonElement element,
        bool enforceUiInvariants,
        ref int nodes,
        ref int keyedCount,
        ref int errors,
        HashSet<string> seenKeys,
        List<string> violations)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                string? kindStr = null;
                if (element.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
                {
                    kindStr = kindEl.GetString();
                }

                var isNode = kindStr is not null
                    || element.TryGetProperty("type", out _)
                    || element.TryGetProperty("$type", out _);

                if (isNode)
                {
                    nodes++;
                    if (string.Equals(kindStr, "errorLine", StringComparison.Ordinal)
                        || (element.TryGetProperty("tone", out var tone)
                            && tone.ValueKind == JsonValueKind.String
                            && string.Equals(tone.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("severity", out var sev)
                            && sev.ValueKind == JsonValueKind.String
                            && string.Equals(sev.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("status", out var status)
                            && status.ValueKind == JsonValueKind.String
                            && string.Equals(status.GetString(), "crashed", StringComparison.OrdinalIgnoreCase))
                        || (string.Equals(kindStr, "progress", StringComparison.Ordinal)
                            && element.TryGetProperty("showError", out var showErr)
                            && showErr.ValueKind == JsonValueKind.True))
                    {
                        errors++;
                    }

                    if (enforceUiInvariants && kindStr is not null)
                    {
                        ValidateUiNodeInvariants(element, kindStr, ref keyedCount, seenKeys, violations);
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(kindStr, "neuronRef", StringComparison.Ordinal)
                        && string.Equals(prop.Name, "props", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    WalkViewElement(prop.Value, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkViewElement(item, enforceUiInvariants, ref nodes, ref keyedCount, ref errors, seenKeys, violations);
                }

                break;
        }
    }

    private static void ValidateUiNodeInvariants(
        JsonElement node,
        string kindStr,
        ref int keyedCount,
        HashSet<string> seenKeys,
        List<string> violations)
    {
        if (string.Equals(kindStr, "appShell", StringComparison.Ordinal))
        {
            if (!TryGetNonBlankString(node, "key", out var shellKey))
            {
                violations.Add("AppShellNode has an empty 'key'.");
            }
            else
            {
                keyedCount++;
                if (!seenKeys.Add(shellKey))
                {
                    violations.Add($"Duplicate UI node key '{shellKey}' on 'appShell'.");
                }
            }

            if (!TryGetNonBlankString(node, "automationName", out _))
            {
                violations.Add("AppShellNode has an empty 'automationName'.");
            }

            if (!node.TryGetProperty("body", out var bodyEl)
                || bodyEl.ValueKind != JsonValueKind.Object
                || !TryGetNonBlankString(bodyEl, "kind", out var bodyKind))
            {
                violations.Add("AppShellNode has a null or invalid 'body' layout.");
            }
            else if (ContainerNodeKinds.Contains(bodyKind)
                && (!bodyEl.TryGetProperty("children", out var bodyChildren)
                    || bodyChildren.ValueKind != JsonValueKind.Array
                    || bodyChildren.GetArrayLength() == 0))
            {
                violations.Add($"AppShellNode body '{bodyKind}' rendered 0 children (broken empty shell layout).");
            }

            return;
        }

        if (KeyedUiNodeKinds.Contains(kindStr))
        {
            if (!TryGetNonBlankString(node, "key", out var nodeKey))
            {
                violations.Add($"UI node '{kindStr}' is missing a non-empty 'key'.");
            }
            else
            {
                keyedCount++;
                if (!seenKeys.Add(nodeKey))
                {
                    violations.Add($"Duplicate UI node key '{nodeKey}' on '{kindStr}'.");
                }
            }

            if (!TryGetNonBlankString(node, "automationName", out _))
            {
                violations.Add($"UI node '{kindStr}' is missing a non-empty 'automationName'.");
            }
        }
    }

    private static bool TryGetNonBlankString(JsonElement obj, string propertyName, out string value)
    {
        if (obj.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String
            && prop.GetString() is { } raw
            && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool IsUnhostedScreenException(Exception ex)
        => ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No registration", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("app.engineer", StringComparison.OrdinalIgnoreCase);

    private async Task<EngineerLiveProbe> ProbeHttpEndpointAsync(
        IHttpClientFactory? factory,
        HttpClient? fallbackClient,
        string name,
        string kind,
        string url,
        CancellationToken ct)
    {
        try
        {
            if (factory is not null)
            {
                using var client = factory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                var ok = code is >= 200 and < 400;
                return new EngineerLiveProbe(name, kind, ok, code, $"GET {url} -> HTTP {code}");
            }

            if (fallbackClient is not null)
            {
                using var response = await fallbackClient.GetAsync(new Uri(url), ct).ConfigureAwait(false);
                var code = (int)response.StatusCode;
                var ok = code is >= 200 and < 400;
                return new EngineerLiveProbe(name, kind, ok, code, $"GET {url} -> HTTP {code}");
            }

            return new EngineerLiveProbe(name, kind, false, 503, "No HTTP client configured.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Live HTTP probe '{name}' failed against '{url}'.");
            return new EngineerLiveProbe(name, kind, false, 503, $"Probe failed: {ex.Message}");
        }
    }

    private sealed record ScreenViewAudit(
        int NodeCount,
        int KeyedCount,
        int ErrorNodeCount,
        IReadOnlyList<string> Violations);

    private sealed record CapturedSpan(
        string SourceName,
        string OperationName,
        string OtelTraceId,
        string? BrainTraceId,
        bool IsError,
        string? StatusDescription,
        long DurationMs);
}
