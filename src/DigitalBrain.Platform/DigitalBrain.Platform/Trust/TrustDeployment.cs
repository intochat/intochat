using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Trust;

// R75: what this machine remembers of one trust deployment (seen head versions, part journals, the signed-call
// counter) lives in {MachineDirectory}/{deploymentId}, so a fresh trust deployment meets a clean memory while a
// rollback of the same deployment still trips. The identity key stays at the root: it is the machine's own.
internal sealed class TrustDeployment(INodeDirectoryClient directory, IOptions<PartsOptions> options)
{
    private readonly Lock _gate = new();
    private Lazy<Task<string>>? _resolving;
    private string? _directory;

    // The deployment's folder; resolved at registration, before any signed call or part open.
    public string Directory => _directory
        ?? throw new InvalidOperationException("The trust deployment is not resolved yet; the machine registers before it signs or opens a part.");

    public Task<string> Resolve()
    {
        Lazy<Task<string>> resolving;
        lock (_gate) { resolving = _resolving ??= new(Load); }
        return Await(resolving);
    }

    private async Task<string> Await(Lazy<Task<string>> resolving)
    {
        try { return await resolving.Value; }
        catch
        {
            lock (_gate) { if (ReferenceEquals(_resolving, resolving)) { _resolving = null; } }
            throw;
        }
    }

    private async Task<string> Load()
    {
        var id = await directory.DeploymentId();
        return _directory = Path.Combine(options.Value.MachineDirectory, id);
    }
}
