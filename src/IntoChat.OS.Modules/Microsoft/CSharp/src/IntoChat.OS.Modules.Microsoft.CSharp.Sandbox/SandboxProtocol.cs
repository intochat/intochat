using System.Net.Http;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;

internal static class SandboxProtocol
{
    public const string ReportKindTag = "intochatos.runtime.report";
    public const string HeartbeatReport = "heartbeat";
    public static readonly HttpRequestOptionsKey<string> ReportKind = new(ReportKindTag);
}
