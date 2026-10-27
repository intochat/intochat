namespace IntoChat.OS.Runtime;

// Transitional (06-09, D-04): the one path from os code outside the kernel composition to a class that still derives Orleans
// Grain (the NeuronsStillGrains.json allow-list: Authority's raw grains, the creator model worker, test probes). The kernel
// composition implements it and refuses any contract no such class implements, so module code holds no grain factory and
// cannot reach a plain neuron's host grain through it. It goes with the allow-list in Phase 12.
public interface IStillGrains
{
    // A proxy of contract for the still-grain keyed by a string.
    /// <exception cref="InvalidOperationException">No allow-listed still-grain class implements contract.</exception>
    object Get(Type contract, string key);

    // A proxy of contract for the still-grain keyed by an integer (stateless workers).
    /// <exception cref="InvalidOperationException">No allow-listed still-grain class implements contract.</exception>
    object Get(Type contract, long key);

    T Get<T>(string key) where T : class => (T)Get(typeof(T), key);

    T Get<T>(long key) where T : class => (T)Get(typeof(T), key);
}
