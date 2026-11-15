using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Mcp;

/// <summary>
/// Shared Debug-level console logger factory used by MCP tool components only when constructed
/// outside DI (for example in standalone unit tests).
/// </summary>
internal static class McpConsoleLog
{
    private static readonly ILoggerFactory Factory =
        LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());

    public static ILogger For<T>() => Holder<T>.Instance;

    private static class Holder<T>
    {
        public static readonly ILogger Instance = Factory.CreateLogger<T>();
    }
}
