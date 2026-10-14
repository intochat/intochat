using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.Modules.Apps.Tests.Runtimes;

public sealed class AppReactivationFacts
{
    [Fact]
    public async Task Reactivation_reassigns_stored_sources_as_the_owner_without_changing_the_app()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RestoringRuntime();
        await using var brain = await TestBrain.Create().WithModule<AppsModule>()
            .ConfigureServices(services => { services.RemoveAll<IAppRuntime>(); services.AddSingleton<IAppRuntime>(runtime); })
            .StartAsync(ct);
        var alice = await brain.PersonAsync("restore-alice", ct);
        var key = IApp.Key(alice.BrainId, "retained");
        var app = brain.Get<IApp>(key);
        using (alice.AsCaller())
        {
            await app.Create(new("Retained", "", [], [new("Feed", "", "initial")], []), "kept spec");
            await app.WriteBehavior("view.cs", "// saved source", Guid.NewGuid());
            await app.Configure(new() { ["Feed"] = "chosen" }, new() { ["tables"] = "saved-binding" });
        }
        var before = await app.Read();
        runtime.Block = true;
        await brain.DeactivateAsync(app, ct);
        await app.Read();
        try
        {
            var restored = await runtime.Restoring.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(alice.AccountId, restored.Caller?.AccountId);
            Assert.Equal(alice.PrincipalId, restored.Caller?.PrincipalId);
            Assert.Equal("// saved source", restored.Sources["view.cs"]);
            Assert.Equal("chosen", restored.Configuration["Feed"]);
            Assert.Equal("saved-binding", restored.Configuration["Account__tables"]);
            // Runtime callbacks and ordinary app reads cannot be blocked behind the restore assignment.
            await using var watch = await brain.Observe<AppChanged>(app, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            var whileRestoring = await app.Read().WaitAsync(TimeSpan.FromSeconds(2), ct);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(whileRestoring));
        }
        finally { runtime.Release.TrySetResult(); }
    }

    [Fact]
    public async Task A_restore_queued_during_an_edit_uses_the_new_program_after_the_edit_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new RestoringRuntime();
        await using var brain = await TestBrain.Create().WithModule<AppsModule>()
            .ConfigureServices(services => { services.RemoveAll<IAppRuntime>(); services.AddSingleton<IAppRuntime>(runtime); })
            .StartAsync(ct);
        var key = IApp.Key("owner", "ordered");
        var app = brain.Get<IApp>(key);
        await app.Create(new("Ordered", "", [], [], []), "spec");
        await app.WriteBehavior("view.cs", "// old", Guid.NewGuid());
        runtime.Block = true;
        var edit = app.WriteBehavior("view.cs", "// replacement", Guid.NewGuid());
        await runtime.Restoring.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        runtime.Block = false;
        try
        {
            await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IAppDeploy>(key).Restore();
            await app.Read();
        }
        finally { runtime.Release.TrySetResult(); }
        await edit;
        // This normal request is queued after Restore and completes only after its serialized assignment.
        await app.CurrentBuildAttempt();
        Assert.Equal("// replacement", runtime.Assignments.Last().Sources["view.cs"]);
        Assert.Equal("// replacement", (await app.Read()).Files["view.cs"]);
    }

    private sealed record Deployment(Dictionary<string, string> Sources, Dictionary<string, string> Configuration, CallerContext? Caller);

    private sealed class RestoringRuntime : IAppRuntime
    {
        public bool Block;
        public ConcurrentQueue<Deployment> Assignments { get; } = new();
        public TaskCompletionSource<Deployment> Restoring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            var assignment = new Deployment(new(sources), new(configuration), CallerContextStamper.TryGet(out var caller) ? caller : null);
            Assignments.Enqueue(assignment);
            if (Block)
            {
                Restoring.TrySetResult(assignment);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return sources.Keys.ToDictionary(path => path, path => "hash:" + path, StringComparer.Ordinal);
        }

        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
