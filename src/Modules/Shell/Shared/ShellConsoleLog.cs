using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter;

/// <summary>
/// Shared Debug-level console logger factory used by Shell components only when no DI
/// <see cref="ILogger"/> or <see cref="ILoggerFactory"/> was supplied by a caller or host.
/// </summary>
internal static class ShellConsoleLog
{
    private static readonly ILoggerFactory Factory =
        LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());

    public static ILogger For<T>() => Holder<T>.Instance;

    public static ILogger For(Type owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Factory.CreateLogger(owner.FullName ?? owner.Name);
    }

    private static class Holder<T>
    {
        public static readonly ILogger Instance = Factory.CreateLogger<T>();
    }
}
