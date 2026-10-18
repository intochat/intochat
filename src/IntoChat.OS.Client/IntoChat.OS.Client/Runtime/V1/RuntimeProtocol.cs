using System.Text.Json;

namespace IntoChat.OS.Client.Runtime.V1;

// The wire between a brain and its sandbox session, shared by the silo edge, this client and the
// sandbox host. The host never holds an Orleans connection: three HTTP routes and a session token.
public static class RuntimeProtocol
{
    public const string Invoke = "runtime/v1/invoke";
    public const string Report = "runtime/v1/report";
    public const string Stream = "runtime/v1/stream";
    // brain.Kernel travels as invocations of this reserved contract: Instances(contract, type), Types(), Running() and Brains().
    public const string KernelContract = "IntoChat.OS.IKernelView";
    public const string KernelInstances = "Instances";
    public const string KernelRunning = "Running";
    public const string KernelTypes = "Types";
    public const string KernelBrains = "Brains";
    public const string RefusalHeader = "X-IntoChatOS-Refusal";
    // Both ends tag a heartbeat report's span with this, so telemetry keeps heartbeats quiet and every other report traced.
    public const string ReportKindTag = "intochatos.runtime.report";
    public const string HeartbeatReport = "heartbeat";
    public const int MaximumAcknowledgements = 256;
    public static readonly HttpRequestOptionsKey<string> ReportKind = new(ReportKindTag);
    // A bounded UI tree can contain 32 nodes, each with a children array, inside an invocation envelope.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
}

[GenerateSerializer, Alias("microsoft.csharp.runtime.runtime-invocation")]
public sealed record RuntimeInvocation([property: Id(0)] string Behavior, [property: Id(1)] string Contract, [property: Id(2)] string Key, [property: Id(3)] string Method, [property: Id(4)] JsonElement[] Arguments);

// Exactly one of the optional members is set per report.
public sealed record RuntimeReport(
    string? Fingerprint = null,
    SubscribeReport? Subscribe = null,
    AcknowledgeReport? Acknowledge = null,
    BehaviorStatusReport? Status = null,
    LogsReport? Logs = null,
    bool Heartbeat = false,
    AcknowledgeReport[]? Acknowledgements = null);
[GenerateSerializer, Alias("microsoft.csharp.runtime.subscribe-report")]
public sealed record SubscribeReport([property: Id(0)] string Behavior, [property: Id(1)] string Contract, [property: Id(2)] string Key, [property: Id(3)] string Signal, [property: Id(4)] string? Selector = null);
[GenerateSerializer, Alias("microsoft.csharp.runtime.acknowledge-report")]
// CpuMicroseconds is the whole sandbox process's processor time spent while the synapse handled the acknowledged
// batches, WallMicroseconds the time it took, Batches how many batches this report acknowledges at once, Skipped
// that the synapse passed over every signal of them (another operation of its app): delivered, but not a run.
public sealed record AcknowledgeReport([property: Id(0)] string Subscription, [property: Id(1)] long Watermark, [property: Id(2)] long CpuMicroseconds = 0,
    [property: Id(3)] long WallMicroseconds = 0, [property: Id(4)] int Batches = 1, [property: Id(5)] bool Skipped = false);
[GenerateSerializer, Alias("microsoft.csharp.runtime.behavior-status-report")]
public sealed record BehaviorStatusReport([property: Id(0)] string Behavior, [property: Id(1)] string Hash, [property: Id(2)] string Status, [property: Id(3)] int? ExitCode, [property: Id(4)] string? Error,
    [property: Id(5)] string? AssignmentRevision = null);
[GenerateSerializer, Alias("microsoft.csharp.runtime.logs-report")]
public sealed record LogsReport([property: Id(0)] string Request, [property: Id(1)] string Text);

// Stream events: the event name is the record's Kind.
[GenerateSerializer, Alias("microsoft.csharp.runtime.assignment-event")]
public sealed record AssignmentEvent([property: Id(0)] string Behavior, [property: Id(1)] string Hash, [property: Id(2)] Uri Assembly, [property: Id(3)] Uri Symbols, [property: Id(4)] Dictionary<string, string> Settings,
    [property: Id(5)] string? Revision = null)
{ public const string Kind = "assign"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.retire-event")]
public sealed record RetireEvent([property: Id(0)] string Behavior) { public const string Kind = "retire"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.subscribed-event")]
public sealed record SubscribedEvent([property: Id(0)] string Behavior, [property: Id(1)] string Contract, [property: Id(2)] string Key, [property: Id(3)] string Signal, [property: Id(4)] string Subscription) { public const string Kind = "subscribed"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.signal-batch-event")]
// TraceParent is the W3C trace of the latest arrival, so the calls a behavior makes in reaction join its trace.
public sealed record SignalBatchEvent([property: Id(0)] string Subscription, [property: Id(1)] string[] Signals, [property: Id(2)] long Watermark, [property: Id(3)] string? TraceParent = null) { public const string Kind = "signals"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.token-event")]
public sealed record TokenEvent([property: Id(0)] string Token) { public const string Kind = "token"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.ready-event")]
// Behaviors is the brain's whole desired set: a host that reconnects unloads whatever it holds beyond it.
public sealed record ReadyEvent([property: Id(0)] string Session, [property: Id(1)] string[] Behaviors) { public const string Kind = "ready"; }
[GenerateSerializer, Alias("microsoft.csharp.runtime.logs-request-event")]
public sealed record LogsRequestEvent([property: Id(0)] string Behavior, [property: Id(1)] int Tail, [property: Id(2)] string Request) { public const string Kind = "logs"; }

// What the brain posts to the sandbox host to open (or reopen) its session: the host dials EdgeUrl back.
[GenerateSerializer, Alias("microsoft.csharp.runtime.session-start")]
public sealed record SessionStart([property: Id(0)] string Brain, [property: Id(1)] string Session, [property: Id(2)] string Token, [property: Id(3)] string EdgeUrl);

// What the brain posts to its session to judge a signal against an app's rules: the rules library by
// hash (fetched like a behavior), the signal as JSON with its type, and the time the rules may take.
// Each verdict names its rule.
public sealed record JudgeRequest(string Hash, Uri Assembly, string SignalType, JsonElement Signal, TimeSpan Budget);
public sealed record JudgeVerdict(string Rule, bool Allowed, string Reason);
public sealed record JudgeAnswer(JudgeVerdict[] Verdicts);
