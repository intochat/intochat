using System.Net.Http.Json;
using DigitalBrain.Apps;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Microsoft.CSharp;

namespace DigitalBrain.OS.Tests.E2E;

// The publish gate for the OS's own apps. The host publishes its embedded bundles as verified by
// the build, so this is where their tests.cs runs: in the real sandbox, against real contracts.
// Needs a Docker daemon with Linux containers.
public sealed class ShippedAppVerificationFacts
{
    [Fact]
    public async Task EveryOsAppPassesItsTestsInTheSandbox()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = deadline.Token;
        await using var brain = await ReferenceBrain.Create()
            .ConfigureModule<CSharpModule, CSharpOptions>(csharp => csharp.WithSandbox(RepositoryRoot()))
            .StartAsync(ct);
        var bundles = new EmbeddedShippedAppSource(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os").Load();
        Assert.NotEmpty(bundles);

        foreach (var shipped in bundles)
        {
            // A copy under its own name: the brain already has the installed default.
            var bundle = shipped with { Name = "verify-" + shipped.Name };
            (await brain.HttpClient.PostAsJsonAsync("/brains/shipped/apps/import", bundle, ct)).EnsureSuccessStatusCode();
            (await brain.HttpClient.PostAsync($"/brains/shipped/apps/{bundle.Name}/verify", null, ct)).EnsureSuccessStatusCode();

            var app = brain.Get<IApp>(IApp.Key(BrainScope.Create("owner", "shipped").Id, bundle.Name));
            // The verify job records its verdicts when the run ends; an import carries none.
            var snapshot = await app.Read();
            while (snapshot.Verdicts.Length == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                snapshot = await app.Read();
            }
            Assert.True(snapshot.Verdicts.All(verdict => verdict.Passed),
                $"{shipped.Name}: " + string.Join("; ", snapshot.Verdicts.Select(verdict => $"{verdict.Name}={(verdict.Passed ? "pass" : "FAIL " + verdict.Message)}")));
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
