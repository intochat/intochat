using System.Diagnostics;

/// <summary>
/// Coordinates clean kernel and shell teardown when Windows ends the session or updates the MSIX package
/// (<c>WM_QUERYENDSESSION</c> / <c>WM_ENDSESSION</c> / <c>WM_CLOSE</c>) or when a console/POSIX termination signal arrives.
/// </summary>
internal sealed class DesktopShutdownCoordinator : IDisposable
{
    public const uint WmClose = 0x0010;
    public const uint WmQueryEndSession = 0x0011;
    public const uint WmEndSession = 0x0016;

    public const uint CtrlCEvent = 0;
    public const uint CtrlBreakEvent = 1;
    public const uint CtrlCloseEvent = 2;
    public const uint CtrlLogoffEvent = 5;
    public const uint CtrlShutdownEvent = 6;

    private readonly Action _onShutdown;
    private readonly TimeSpan _waitTimeout;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ManualResetEventSlim _completed = new(initialState: false);
    private int _triggered;
    private int _disposed;

    public DesktopShutdownCoordinator(Action onShutdown, TimeSpan? waitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(onShutdown);
        _onShutdown = onShutdown;
        _waitTimeout = waitTimeout ?? TimeSpan.FromSeconds(5);
    }

    public bool ShutdownTriggered => Volatile.Read(ref _triggered) != 0;

    public CancellationToken ShutdownToken => _shutdownCts.Token;

    /// <summary>
    /// Handles top-level Win32 session/close messages. Returns <see langword="true"/> when the message was handled
    /// and <paramref name="result"/> should be returned directly from <c>WndProc</c>.
    /// </summary>
    public bool HandleWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        _ = lParam;

        switch (message)
        {
            case WmQueryEndSession:
                // Acknowledge session end / MSIX package update immediately; actual teardown occurs on WM_ENDSESSION.
                result = 1;
                return true;

            case WmEndSession:
                if (wParam != 0)
                {
                    TriggerShutdownAndWait();
                }

                result = 0;
                return true;

            case WmClose:
                TriggerShutdownAndWait();
                result = 0;
                return true;

            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// Handles Windows console control events (<c>SetConsoleCtrlHandler</c>).
    /// </summary>
    public bool HandleConsoleCtrlEvent(uint ctrlType)
    {
        switch (ctrlType)
        {
            case CtrlCEvent:
            case CtrlBreakEvent:
            case CtrlCloseEvent:
            case CtrlLogoffEvent:
            case CtrlShutdownEvent:
                TriggerShutdownAndWait();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Triggers the registered shutdown callback exactly once and waits up to the configured timeout for completion.
    /// </summary>
    public void TriggerShutdownAndWait()
    {
        if (Interlocked.Exchange(ref _triggered, 1) == 0)
        {
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _shutdownCts.Cancel();
                }
            }
            catch (ObjectDisposedException ex)
            {
                Trace.TraceWarning("Shutdown cancellation token already disposed: {0}", ex.Message);
            }

            try
            {
                _onShutdown();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Desktop shutdown handler encountered an error: {0}", ex.Message);
            }
            finally
            {
                try
                {
                    _completed.Set();
                }
                catch (ObjectDisposedException ex)
                {
                    Trace.TraceWarning("Shutdown completion barrier already disposed: {0}", ex.Message);
                }
            }

            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ = _completed.Wait(_waitTimeout);
            }
        }
        catch (ObjectDisposedException ex)
        {
            Trace.TraceWarning("Shutdown completion barrier disposed while waiting: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _completed.Dispose();
        _shutdownCts.Dispose();
    }
}
