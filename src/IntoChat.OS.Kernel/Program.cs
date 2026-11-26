using IntoChat.OS.Kernel;

WebApplication? app = null;
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddIntoChatOSKernel();

    app = builder.Build();
    app.MapIntoChatOSKernel();

    Environment.ExitCode = await app.RunIntoChatOSKernelAsync(args);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // Return a clean non-zero exit code instead of letting an unhandled exception hit CoreCLR's
    // PROCAbort -> glibc abort() path, which on Linux x86_64 PID 1 degrades into SIGSEGV (exit 139).
    app?.Logger.LogCritical(ex, "IntoChat OS kernel terminated with a fatal startup error.");
    await Console.Error.WriteLineAsync($"Fatal startup error: {ex}");
    await Console.Error.FlushAsync();
    Environment.ExitCode = 1;
}
finally
{
    if (app is not null)
    {
        await app.DisposeAsync();
    }
}
