using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter;

/// <summary>
/// Shared Debug-level console logger factory used by Shell components only when no DI
/// <see cref="ILogger"/> or <see cref="ILoggerFactory"/> was supplied by a caller or host.
/// </summary>
internal static class ShellConsoleLog
{
    public static ILogger For<T>() => IntoChat.OS.Host.Logging.OsConsoleLog.For<T>();

    public static ILogger For(Type owner) => IntoChat.OS.Host.Logging.OsConsoleLog.For(owner);
}
