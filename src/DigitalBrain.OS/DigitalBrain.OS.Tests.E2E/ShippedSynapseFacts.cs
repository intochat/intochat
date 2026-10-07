using DigitalBrain.Framework;
using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.OS.Tests.E2E;

// The OS's own apps run in the real sandbox against real contracts. Needs a Docker daemon with
// Linux containers.
public sealed class ShippedSynapseFacts
{
    [Fact]
    public async Task EveryShippedSynapseIsAliveAndReadsAsASynapse()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        await using var brain = await ReferenceBrain.Create()
            .ConfigureModule<CSharpModule, CSharpOptions>(csharp => csharp.WithSandbox(RepositoryRoot()))
            .StartAsync(ct);
        var scope = BrainScope.Create("owner", "shipped").Id;
        // Any brain-addressed request establishes the brain; Establish provisions the OS defaults.
        (await brain.HttpClient.GetAsync("/brains/shipped/apps", ct)).EnsureSuccessStatusCode();
        foreach (var name in new[] { "settings", "assistant", "datavisualizer" })
        {
            var app = await brain.Get<IApp>(IApp.Key(scope, name)).Read();
            var synapses = app.Files.Keys.Where(AppFiles.IsSynapse).ToArray();
            Assert.NotEmpty(synapses);
            foreach (var path in synapses)
            {
                // Deployed programs live under the runtime's hashed key for the app's current program
                // generation; the generation is app state, so probe the first few.
                var key = Enumerable.Range(0, 8).Select(generation => AppProgramKey.For(IApp.Key(scope, name), generation, path))
                    .FirstOrDefault(candidate => brain.Get<ICSharpFile>(candidate).Inspect(ct).GetAwaiter().GetResult().Source.Length > 0)
                    ?? throw new Xunit.Sdk.XunitException($"{name}/{path} has no deployed program in generations 0-7");
                var synapse = await brain.Get<ICSharpFile>(key).Inspect(ct);
                while (synapse.Status != CSharpFileStatus.Running)
                {
                    // The sandbox builds the first run; poll until it is.
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    synapse = await brain.Get<ICSharpFile>(key).Inspect(ct);
                }
                Assert.NotEmpty(synapse.Source);
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx"))) { return directory.FullName; }
        }
        throw new InvalidOperationException("The sandbox mounts the repository; run the test from inside it.");
    }
}
