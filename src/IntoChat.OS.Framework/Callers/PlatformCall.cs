using DigitalBrain;
using DigitalBrain.Access;

namespace IntoChat.OS.Framework;

// The one place os platform code acts as the kernel: releasing a provider credential or registration to module code. A
// callee that serves only the kernel (IBrainEntries.Release, IIntegration.Release) then reads the stamped caller, never an argument.
internal static class PlatformCall
{
    public static async Task<T> AsKernel<T>(this EdgeAuthority edge, BrainId brain, Func<Task<T>> call)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(call);
        using (edge.EnterKernel(brain))
        {
            return await call().ConfigureAwait(false);
        }
    }
}
