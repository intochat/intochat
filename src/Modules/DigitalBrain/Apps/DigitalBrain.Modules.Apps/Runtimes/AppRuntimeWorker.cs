using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Apps;

[GrainType("apps.runtime-worker"), StatelessWorker, Reentrant]
internal sealed class AppRuntimeWorker(IEnumerable<IAppRuntime> runtimes) : Grain, IAppRuntimeWorker
{
    public Task Answer(string appKey, PackageRevisionRef revisionRef, AppInvocation invocation)
        => AnswerWith(appKey, invocation, async () =>
            (await GrainFactory.GetGrain<IPackage>(revisionRef.Package.ToString()).ReadRevision(revisionRef.Revision)).Content);

    public Task AnswerLive(string appKey, AppInvocation invocation)
        => AnswerWith(appKey, invocation, async () =>
        {
            var content = await GrainFactory.GetGrain<IApp>(appKey).ReadContent();
            return new PackageContent(content.Manifest, new Dictionary<string, string>(content.Files, StringComparer.Ordinal));
        });

    private async Task AnswerWith(string appKey, AppInvocation invocation, Func<Task<PackageContent>> program)
    {
        var app = GrainFactory.GetGrain<IApp>(appKey);
        string? output = null;
        string? error = null;
        try
        {
            var snapshot = await app.Read();
            var content = await program();
            var runtimeName = content.Manifest.RuntimeName;
            var runtime = runtimes.FirstOrDefault(candidate => candidate.Name == runtimeName)
                ?? throw new InvalidOperationException($"This brain has no '{runtimeName}' app runtime.");
            output = await runtime.Answer(new(appKey, invocation.Id, invocation.Operation, invocation.Input, content, snapshot.Settings), CancellationToken.None);
        }
        catch (Exception failure)
        {
            error = failure.Message;
        }
        // Nobody awaits a one-way call, so a rejected answer must still complete the invocation.
        try { await app.Respond(new AppResponse(invocation.Id, output, error)); }
        catch (ArgumentException rejected) { await app.Respond(new AppResponse(invocation.Id, null, rejected.Message)); }
    }
}
