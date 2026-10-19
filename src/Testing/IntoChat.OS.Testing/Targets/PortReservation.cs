using System.Net;
using System.Net.Sockets;

namespace IntoChat.OS.Testing;

// Orleans refuses port 0 for the silo and gateway, so an in-process brain needs concrete ports. A reservation picks them
// from the OS, holds the sockets while it reads them and takes a per-port lock file in the temp directory, so a second
// brain in this or another test process never gets a pair that is still waiting to be bound. The lock stays held until
// the silo has started. Residual race: a process that does not take part binds a port between ReleaseSockets and the
// Orleans bind; InProcessTarget's StartAttempts retry covers that.
internal sealed class PortReservation : IDisposable
{
    private static readonly Lock Gate = new();
    private readonly List<TcpListener> _sockets = [];
    private readonly List<FileStream> _locks = [];

    private PortReservation(int[] ports) => Ports = ports;

    public int[] Ports { get; }

    // A reservation over ports the caller already owns; it holds no socket and no lock.
    internal static PortReservation Of(params int[] ports) => new(ports);

    public static PortReservation Reserve(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var directory = Path.Combine(Path.GetTempPath(), "intochat-ports");
        Directory.CreateDirectory(directory);
        var reservation = new PortReservation(new int[count]);
        try
        {
            lock (Gate)
            {
                for (var taken = 0; taken < count;)
                {
                    var socket = new TcpListener(IPAddress.Loopback, 0);
                    socket.Start();
                    var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                    var file = TryLock(Path.Combine(directory, $"{port}.lock"));
                    if (file is null) { socket.Stop(); continue; }
                    reservation._sockets.Add(socket);
                    reservation._locks.Add(file);
                    reservation.Ports[taken++] = port;
                }
            }
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    // Just before the silo binds: the lock files still keep every cooperating process off these ports.
    public void ReleaseSockets()
    {
        foreach (var socket in _sockets) { socket.Stop(); }
        _sockets.Clear();
    }

    // After the silo has started (or failed to), the ports are the silo's or free again.
    public void ReleaseLocks()
    {
        foreach (var file in _locks) { file.Dispose(); }
        _locks.Clear();
    }

    public void Dispose()
    {
        ReleaseSockets();
        ReleaseLocks();
    }

    private static FileStream? TryLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
