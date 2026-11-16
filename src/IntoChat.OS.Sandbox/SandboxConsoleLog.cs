using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Sandbox;

/// <summary>
/// Debug-level console logger factory that routes all log levels to stderr so
/// <c>--unit-host</c> anonymous stdout pipes remain strictly reserved for NDJSON protocol frames.
/// </summary>
internal static class SandboxConsoleLog
{
    private static readonly ILoggerFactory Factory =
        LoggerFactory.Create(static builder =>
            builder.SetMinimumLevel(LogLevel.Debug)
                   .AddConsole(static options => options.LogToStandardErrorThreshold = LogLevel.Trace));

    public static ILogger<T> For<T>() => Holder<T>.Instance;

    private static class Holder<T>
    {
        public static readonly ILogger<T> Instance = Factory.CreateLogger<T>();
    }
}
