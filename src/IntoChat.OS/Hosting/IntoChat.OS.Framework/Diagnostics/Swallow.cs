// os-only: os logging convention for deliberately swallowed failures; the framework has no equivalent yet.
using DigitalBrain.Log;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Framework.Diagnostics;

// A deliberately swallowed failure stays observable: the exception object and a fixed
// description reach the log at Debug. The description is never built from payloads, tool
// arguments, secrets or the exception message.
public static class Swallow
{
    public static void LogSwallowed(this ILogger logger, Exception exception, string what)
        => logger.LogDebug(exception, "Swallowed failure: {What}", what);

    public static async Task TryAsync(Func<Task> work, ILogger logger, string what)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogSwallowed(exception, what);
        }
    }
}
