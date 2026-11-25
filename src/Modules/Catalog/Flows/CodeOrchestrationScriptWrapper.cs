using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Wraps standalone C# orchestration scripts targeting <c>net11.0</c> against typed
/// <see cref="IntoChat.Sdk.ISandboxBrain"/> proxies (<c>os.Get&lt;TContract&gt;(key)</c>) into a
/// sandbox-runnable <see cref="AppPackage"/>, and parses structured <c>result.json</c> outputs.
/// </summary>
internal static class CodeOrchestrationScriptWrapper
{
    public const string Command = "orchestrate";
    public const string AppKeyPrefix = "orch-";

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Derives a deterministic sandbox app key for a flow run.</summary>
    public static string AppKey(string flowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowKey);
        return AppKeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(flowKey)))[..12];
    }

    /// <summary>Strips optional Markdown code fences from an LLM-generated C# script.</summary>
    public static string StripCodeFences(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return text;
        }

        var body = text[(firstNewLine + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            body = body[..closingFence];
        }

        return body.Trim();
    }

    /// <summary>
    /// Packages a standalone C# orchestration script into an <see cref="AppPackage"/> that executes inside
    /// <c>IntoChat.OS.Sandbox</c> (<c>--unit-host</c>) with typed <c>os.Get&lt;TContract&gt;(key)</c> proxies.
    /// </summary>
    public static AppPackage BuildPackage(string appKey, string title, string scriptSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptSource);

        var safeTitle = string.IsNullOrWhiteSpace(title) ? "Code orchestration" : title.Replace("\"", "'", StringComparison.Ordinal);
        var usesCancellationToken = scriptSource.Contains("CancellationToken", StringComparison.Ordinal);
        var invokeExpr = usesCancellationToken
            ? "global::OrchestrationScript.RunAsync(Brain, inputJson ?? \"{}\", cancellationToken)"
            : "global::OrchestrationScript.RunAsync(Brain, inputJson ?? \"{}\")";

        var typesFile = """
            #nullable enable
            using System.Collections.Generic;

            public sealed record OrchestrationArtifact(
                string Name,
                string Kind,
                string? Content = null,
                string? Uri = null);

            public sealed record OrchestrationResult(
                string Status,
                string Summary,
                OrchestrationArtifact[]? Artifacts = null,
                Dictionary<string, double>? Metrics = null);
            """;

        var wrapper = $$$"""
            #nullable enable
            using System;
            using System.Collections.Generic;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            namespace CodeOrchestrationHost;

            public sealed record OrchestrationAppState;
            public sealed record OrchestrationAppSettings;

            [App("{{{appKey}}}", Title = "{{{safeTitle}}}")]
            public sealed class CodeOrchestrationApp : BrainApp<OrchestrationAppState, OrchestrationAppSettings>
            {
                private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                };

                [OnCommand("{{{Command}}}", Description = "Execute Plane 1 code orchestration script and return result.json.")]
                public Task<string> Orchestrate(string inputJson, CancellationToken cancellationToken)
                    => FormatAsync({{{invokeExpr}}});

                private static async Task<string> FormatAsync(Task<OrchestrationResult> pending)
                {
                    var result = await pending.ConfigureAwait(false);
                    var normalized = new
                    {
                        status = string.IsNullOrWhiteSpace(result?.Status) ? "succeeded" : result.Status,
                        summary = result?.Summary ?? string.Empty,
                        artifacts = result?.Artifacts ?? [],
                        metrics = result?.Metrics ?? new Dictionary<string, double>(),
                    };
                    return JsonSerializer.Serialize(normalized, ResultJson);
                }

                private static async Task<string> FormatAsync(Task<string> pending)
                {
                    var raw = await pending.ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(raw)
                        ? "{\"status\":\"succeeded\",\"summary\":\"\",\"artifacts\":[],\"metrics\":{}}"
                        : raw;
                }
            }
            """;

        AppSourceFile[] files =
        [
            new("OrchestrationTypes.g.cs", typesFile),
            new("OrchestrationApp.g.cs", wrapper),
            new("Script.cs", scriptSource),
        ];

        var digest = AppPackage.ComputeDigest(appKey, "1.0.0", files, "{}");
        return new AppPackage(
            appKey,
            "1.0.0",
            safeTitle,
            "Plane 1 code orchestration script executed inside the hardened sandbox.",
            "code",
            "Orchestration",
            "brain",
            files,
            AppPackageNeeds.Empty,
            "{}",
            digest);
    }

    /// <summary>
    /// Extracts and validates structured <c>result.json</c> (<c>status</c>, <c>summary</c>, <c>artifacts</c>, <c>metrics</c>)
    /// from the sandbox command response.
    /// </summary>
    public static CodeOrchestrationResult ParseResultJson(string? rawResultJson)
    {
        if (string.IsNullOrWhiteSpace(rawResultJson))
        {
            throw new FormatException("Orchestration script produced empty result.json output.");
        }

        using var outer = JsonDocument.Parse(rawResultJson);
        JsonElement root;
        JsonDocument? innerDoc = null;
        try
        {
            if (outer.RootElement.ValueKind == JsonValueKind.String)
            {
                var unwrapped = outer.RootElement.GetString();
                if (string.IsNullOrWhiteSpace(unwrapped))
                {
                    throw new FormatException("Orchestration script returned an empty result.json string.");
                }

                innerDoc = JsonDocument.Parse(unwrapped);
                root = innerDoc.RootElement;
            }
            else
            {
                root = outer.RootElement;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Orchestration result.json must be a JSON object with status, summary, artifacts, and metrics.");
            }

            var status = ReadString(root, "status") ?? "succeeded";
            var summary = ReadString(root, "summary") ?? string.Empty;
            var artifacts = ReadArtifacts(root);
            var metrics = ReadMetrics(root);

            return new CodeOrchestrationResult(status, summary, artifacts, metrics);
        }
        finally
        {
            innerDoc?.Dispose();
        }
    }

    /// <summary>Serializes a <see cref="CodeOrchestrationResult"/> into canonical <c>result.json</c>.</summary>
    public static string SerializeResultJson(CodeOrchestrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var metricsObject = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var metric in result.Metrics)
        {
            metricsObject[metric.Name] = metric.Value;
        }

        var payload = new
        {
            status = result.Status,
            summary = result.Summary,
            artifacts = result.Artifacts,
            metrics = metricsObject,
        };
        return JsonSerializer.Serialize(payload, WireJson);
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
            }
        }

        return null;
    }

    private static CodeOrchestrationArtifact[] ReadArtifacts(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var items = new List<CodeOrchestrationArtifact>();
            foreach (var elem in prop.Value.EnumerateArray())
            {
                if (elem.ValueKind == JsonValueKind.String)
                {
                    var text = elem.GetString() ?? "";
                    items.Add(new CodeOrchestrationArtifact(text, "text", text));
                }
                else if (elem.ValueKind == JsonValueKind.Object)
                {
                    var name = ReadString(elem, "name") ?? $"artifact-{items.Count + 1}";
                    var kind = ReadString(elem, "kind") ?? "text";
                    var content = ReadString(elem, "content");
                    var uri = ReadString(elem, "uri");
                    items.Add(new CodeOrchestrationArtifact(name, kind, content, uri));
                }
            }

            return [.. items];
        }

        return [];
    }

    private static CodeOrchestrationMetric[] ReadMetrics(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "metrics", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var items = new List<CodeOrchestrationMetric>();
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var metricProp in prop.Value.EnumerateObject())
                {
                    if (metricProp.Value.ValueKind == JsonValueKind.Number && metricProp.Value.TryGetDouble(out var number))
                    {
                        items.Add(new CodeOrchestrationMetric(metricProp.Name, number));
                    }
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in prop.Value.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var name = ReadString(elem, "name");
                    var unit = ReadString(elem, "unit");
                    double value = 0;
                    foreach (var field in elem.EnumerateObject())
                    {
                        if (string.Equals(field.Name, "value", StringComparison.OrdinalIgnoreCase)
                            && field.Value.ValueKind == JsonValueKind.Number
                            && field.Value.TryGetDouble(out var parsed))
                        {
                            value = parsed;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        items.Add(new CodeOrchestrationMetric(name, value, unit));
                    }
                }
            }

            return [.. items];
        }

        return [];
    }
}
