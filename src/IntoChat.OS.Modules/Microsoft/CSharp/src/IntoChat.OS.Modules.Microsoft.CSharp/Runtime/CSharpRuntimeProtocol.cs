using System.Text.Json;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

public static class CSharpRuntimeProtocol
{
    public const string Invoke = "runtime/v1/invoke";
    public const string Report = "runtime/v1/report";
    public const string Stream = "runtime/v1/stream";
    public const string KernelContract = "IntoChat.OS.IKernelView";
    public const string KernelInstances = "Instances";
    public const string KernelRunning = "Running";
    public const string KernelTypes = "Types";
    public const string KernelBrains = "Brains";
    public const string RefusalHeader = "X-DigitalBrain-Refusal";
    public const string LegacyRefusalHeader = "X-IntoChatOS-Refusal";
    public const string ReportKindTag = "intochatos.runtime.report";
    public const string HeartbeatReport = "heartbeat";
    public const int MaximumAcknowledgements = 256;
    public static readonly HttpRequestOptionsKey<string> ReportKind = new(ReportKindTag);
}
