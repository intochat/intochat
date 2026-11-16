using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Mcp;

/// <summary>
/// Shared Debug-level console logger factory used by MCP tool components only when constructed
/// outside DI (for example in standalone unit tests).
/// </summary>
internal static class McpConsoleLog
{
    public static ILogger For<T>() => IntoChat.OS.Framework.Logging.OsConsoleLog.For<T>();
}
