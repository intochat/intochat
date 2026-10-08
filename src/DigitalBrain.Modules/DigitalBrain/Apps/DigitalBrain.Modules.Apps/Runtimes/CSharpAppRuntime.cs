using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Apps;

// Deploys a csharp app: one ICSharpFile per program, bound to the app, configured and
// started. Every deployment gets fresh file keys, so a command retried after a lost save
// rewrites and restarts the same files instead of depending on what the previous
// generation left behind.
internal sealed class CSharpAppRuntime(IGrainFactory grains) : IDeployableRuntime
{
    public string Name => AppFiles.Runtime;

    public string[] Keys(string appKey, int generation, IReadOnlyList<string> paths)
        => [.. paths.Select(path => FileKey(appKey, generation, path))];

    // An edit hot-swaps the file: the run restarts, the subscriptions and their buffers survive.
    public async Task<string[]> Swap(RuntimeDeployment deployment, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        foreach (var (path, source) in deployment.Programs.OrderBy(program => program.Key, StringComparer.Ordinal))
        {
            var key = FileKey(deployment.AppKey, deployment.Generation, path);
            await grains.GetGrain<ICSharpAppBinding>(key).BindApp(deployment.AppKey);
            var file = grains.GetGrain<ICSharpFile>(key);
            await file.Configure(deployment.Configuration);
            await file.Swap(source, cancellationToken);
            keys.Add(key);
        }
        return [.. keys];
    }

    public async Task<string[]> Deploy(RuntimeDeployment deployment, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        foreach (var (path, source) in deployment.Programs.OrderBy(program => program.Key, StringComparer.Ordinal))
        {
            var key = FileKey(deployment.AppKey, deployment.Generation, path);
            await grains.GetGrain<ICSharpAppBinding>(key).BindApp(deployment.AppKey);
            var file = grains.GetGrain<ICSharpFile>(key);
            await file.Write(source);
            await file.Configure(deployment.Configuration);
            await file.Start();
            keys.Add(key);
        }
        return [.. keys];
    }

    // Deleting an already deleted file is harmless, so retries need no bookkeeping.
    public async Task Retire(string appKey, string[] storageKeys, CancellationToken cancellationToken)
    {
        foreach (var key in storageKeys)
        {
            await grains.GetGrain<ICSharpFile>(key).Delete();
        }
    }

    internal static string FileKey(string appKey, int generation, string path) => AppProgramKey.For(appKey, generation, path);
}
