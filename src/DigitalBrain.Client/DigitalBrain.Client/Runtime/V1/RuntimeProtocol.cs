using System.Text.Json;

namespace DigitalBrain.Client.Runtime.V1;

// The wire between a brain and its sandbox session, shared by the silo edge, this client and the
// sandbox host. The host never holds an Orleans connection: three HTTP routes and a session token.
public static class RuntimeProtocol
{
    public const string Invoke = "runtime/v1/invoke";
    public const string Report = "runtime/v1/report";
    public const string Stream = "runtime/v1/stream";
    public const string RefusalHeader = "X-DigitalBrain-Refusal";
    public static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
}

public sealed record RuntimeInvocation(string Behavior, string Contract, string Key, string Method, JsonElement[] Arguments);

// Exactly one of the optional members is set per report.
public sealed record RuntimeReport(
    string? Fingerprint = null,
    SubscribeReport? Subscribe = null,
    AcknowledgeReport? Acknowledge = null,
    BehaviorStatusReport? Status = null,
    LogsReport? Logs = null,
    bool Heartbeat = false);
public sealed record SubscribeReport(string Behavior, string Contract, string Key, string Signal);
public sealed record AcknowledgeReport(string Subscription, long Watermark);
public sealed record BehaviorStatusReport(string Behavior, string Hash, string Status, int? ExitCode, string? Error);
public sealed record LogsReport(string Request, string Text);

// Stream events: the event name is the record's Kind.
public sealed record AssignmentEvent(string Behavior, string Hash, Uri Assembly, Uri Symbols, Dictionary<string, string> Settings) { public const string Kind = "assign"; }
public sealed record RetireEvent(string Behavior) { public const string Kind = "retire"; }
public sealed record SubscribedEvent(string Behavior, string Contract, string Key, string Signal, string Subscription) { public const string Kind = "subscribed"; }
public sealed record SignalBatchEvent(string Subscription, string[] Signals, long Watermark) { public const string Kind = "signals"; }
public sealed record TokenEvent(string Token) { public const string Kind = "token"; }
public sealed record ReadyEvent(string Session) { public const string Kind = "ready"; }
public sealed record LogsRequestEvent(string Behavior, int Tail, string Request) { public const string Kind = "logs"; }

// What the brain posts to the sandbox host to open (or reopen) its session: the host dials EdgeUrl back.
public sealed record SessionStart(string Brain, string Session, string Token, string EdgeUrl);
