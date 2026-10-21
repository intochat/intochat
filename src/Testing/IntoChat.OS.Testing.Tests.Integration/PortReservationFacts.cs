namespace IntoChat.OS.Testing.Tests.Integration;

public sealed class PortReservationFacts
{
    [Fact]
    public async Task ParallelReservationsNeverShareAPort()
    {
        var reservations = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => PortReservation.Reserve(2), TestContext.Current.CancellationToken)));
        try
        {
            var ports = reservations.SelectMany(reservation => reservation.Ports).ToArray();
            Assert.Equal(ports.Length, ports.Distinct().Count());
        }
        finally
        {
            foreach (var reservation in reservations) { reservation.Dispose(); }
        }
    }

    [Fact]
    public void APortWhoseLockFileIsHeldIsSkipped()
    {
        using var first = PortReservation.Reserve(1);
        var held = first.Ports[0];
        first.ReleaseSockets();
        // Only the lock file keeps the port from being handed out again while the socket is free.
        var others = Enumerable.Range(0, 64).Select(_ => PortReservation.Reserve(1)).ToArray();
        try { Assert.DoesNotContain(held, others.SelectMany(other => other.Ports)); }
        finally { foreach (var other in others) { other.Dispose(); } }
    }
}
