using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Host.Logging;

/// <summary>
/// Shared Debug-level console logger factory used only when no DI <see cref="ILogger"/> or
/// <see cref="ILoggerFactory"/> was supplied by a caller or host.
/// </summary>
public static class OsConsoleLog
{
    private static readonly ILoggerFactory Factory =
        LoggerFactory.Create(static builder => builder.SetMinimumLevel(LogLevel.Debug).AddConsole());

    public static ILogger<T> For<T>() => Holder<T>.Instance;

    public static ILogger For(Type owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Factory.CreateLogger(owner.FullName ?? owner.Name);
    }

    private static class Holder<T>
    {
        public static readonly ILogger<T> Instance = Factory.CreateLogger<T>();
    }
}
