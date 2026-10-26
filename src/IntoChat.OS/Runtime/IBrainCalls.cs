namespace IntoChat.OS.Runtime;

// The one way a plain neuron acts in another brain (06-08, R4-21(3)). Synapses stay inside a brain; a cross-brain call is a
// person's act in the other brain: the stamped caller, acting in the target brain, and only when that person's account is a
// member of it. Circles use it (a member's own brain from the circle, the new circle brain from the factory). The kernel
// composition implements it; every call made inside still passes the target neuron's access policy, judged in that brain.
public interface IBrainCalls
{
    // Runs work stamped as the current caller acting in brainId, then restores the caller.
    /// <exception cref="UnauthorizedAccessException">No trusted caller is stamped, or its account is not a member of brainId.</exception>
    Task<T> In<T>(string brainId, Func<Task<T>> work);

    // In, for work with no result.
    /// <exception cref="UnauthorizedAccessException">No trusted caller is stamped, or its account is not a member of brainId.</exception>
    async Task In(string brainId, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        await In(brainId, async () => { await work(); return true; });
    }
}
