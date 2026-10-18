using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Testing.Aspire.Diagnostics;

// OTLP JSON wire models. Aspire's equivalent models are internal shared source, not a public SDK.
// Unknown fields remain available so privacy assertions also inspect future protocol additions.
public abstract class DashboardTelemetryObject
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; init; }
}

public sealed class DashboardTelemetryData : DashboardTelemetryObject
{
    public DashboardResourceSpans[] ResourceSpans { get; init; } = [];
    public DashboardResourceLogs[] ResourceLogs { get; init; } = [];
}

public sealed class DashboardResourceSpans : DashboardTelemetryObject
{
    public DashboardResource? Resource { get; init; }
    public DashboardScopeSpans[] ScopeSpans { get; init; } = [];
    public string? SchemaUrl { get; init; }
}

public sealed class DashboardScopeSpans : DashboardTelemetryObject
{
    public DashboardInstrumentationScope? Scope { get; init; }
    public DashboardSpan[] Spans { get; init; } = [];
    public string? SchemaUrl { get; init; }
}

public sealed class DashboardResourceLogs : DashboardTelemetryObject
{
    public DashboardResource? Resource { get; init; }
    public DashboardScopeLogs[] ScopeLogs { get; init; } = [];
    public string? SchemaUrl { get; init; }
}

public sealed class DashboardScopeLogs : DashboardTelemetryObject
{
    public DashboardInstrumentationScope? Scope { get; init; }
    public DashboardLogRecord[] LogRecords { get; init; } = [];
    public string? SchemaUrl { get; init; }
}

public sealed class DashboardResource : DashboardTelemetryObject
{
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
}

public sealed class DashboardInstrumentationScope : DashboardTelemetryObject
{
    public string? Name { get; init; }
    public string? Version { get; init; }
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
}

public sealed class DashboardKeyValue : DashboardTelemetryObject
{
    public string Key { get; init; } = string.Empty;
    public DashboardAnyValue? Value { get; init; }
}

public sealed class DashboardAnyValue : DashboardTelemetryObject
{
    public string? StringValue { get; init; }
    public bool? BoolValue { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? IntValue { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double? DoubleValue { get; init; }
    public string? BytesValue { get; init; }
    public DashboardArrayValue? ArrayValue { get; init; }
    public DashboardKeyValueList? KvlistValue { get; init; }
}

public sealed class DashboardArrayValue : DashboardTelemetryObject
{
    public DashboardAnyValue[] Values { get; init; } = [];
}

public sealed class DashboardKeyValueList : DashboardTelemetryObject
{
    public DashboardKeyValue[] Values { get; init; } = [];
}

public enum DashboardSpanKind { Unspecified = 0, Internal = 1, Server = 2, Client = 3, Producer = 4, Consumer = 5 }

public enum DashboardStatusCode { Unset = 0, Ok = 1, Error = 2 }

public sealed class DashboardSpan : DashboardTelemetryObject
{
    public string TraceId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;
    public string? TraceState { get; init; }
    public string? ParentSpanId { get; init; }
    public uint Flags { get; init; }
    public string Name { get; init; } = string.Empty;
    public DashboardSpanKind Kind { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong StartTimeUnixNano { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong EndTimeUnixNano { get; init; }
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
    public DashboardSpanEvent[] Events { get; init; } = [];
    public uint DroppedEventsCount { get; init; }
    public DashboardSpanLink[] Links { get; init; } = [];
    public uint DroppedLinksCount { get; init; }
    public DashboardSpanStatus? Status { get; init; }
}

public sealed class DashboardSpanEvent : DashboardTelemetryObject
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong TimeUnixNano { get; init; }
    public string Name { get; init; } = string.Empty;
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
}

public sealed class DashboardSpanLink : DashboardTelemetryObject
{
    public string TraceId { get; init; } = string.Empty;
    public string SpanId { get; init; } = string.Empty;
    public string? TraceState { get; init; }
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
    public uint Flags { get; init; }
}

public sealed class DashboardSpanStatus : DashboardTelemetryObject
{
    public string? Message { get; init; }
    public DashboardStatusCode Code { get; init; }
}

public sealed class DashboardLogRecord : DashboardTelemetryObject
{
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong TimeUnixNano { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public ulong ObservedTimeUnixNano { get; init; }
    public int SeverityNumber { get; init; }
    public string? SeverityText { get; init; }
    public DashboardAnyValue? Body { get; init; }
    public DashboardKeyValue[] Attributes { get; init; } = [];
    public uint DroppedAttributesCount { get; init; }
    public uint Flags { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public string? EventName { get; init; }
}
