namespace IntoChat.OS;

// Optional transport capability: a persistent program has finished its initial reconciliation
// when its outermost signal stream is ready to wait. Inner source-set pumps are not that boundary.
// Supported persistent scripts have one outer signal loop. This is not a whole-program barrier
// for parallel outer loops, arbitrary fire-and-forget tasks, or programs which never subscribe.
// Runtime SPI: IntoChat.OS.Client's connection implements it explicitly; transitional (Phase 4/6).
public interface IInitialSignalWait
{
    IDisposable SuppressInitialWait();
    Task ReachedInitialWaitAsync(CancellationToken cancellationToken);
}
