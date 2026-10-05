using DigitalBrain.AI;
using DigitalBrain.AI.OpenAI;
using DigitalBrain.Apps;
using DigitalBrain.ClickHouse;
using DigitalBrain.Flutter;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Postgres;
using DigitalBrain.Qdrant;
using DigitalBrain.Salesforce;
using DigitalBrain.Supabase;

namespace DigitalBrain.OS.Tests.E2E;

internal static class ProductE2E
{
    public static Task<E2EBrain> StartAsync(CancellationToken ct) => Create().StartAsync(ct);

    // The host ships its first-party apps in the background after it is healthy; a fact that opens
    // one has to wait until that package is published.
    public static async Task WaitUntilShippedAsync(E2EBrain brain, string package, CancellationToken ct, TimeSpan? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget ?? TimeSpan.FromMinutes(4));
        while (true)
        {
            var snapshot = await brain.Get<IPackage>(package).Read();
            if (snapshot.Published is not null) { return; }
            // A red verification never publishes; failing with its verdicts beats a silent timeout.
            if (snapshot.Head is { } head
                && await brain.Get<IAppVerification>(IAppVerification.Key(new(PackageId.Parse(package), head))).Read()
                    is { Green: false } verification)
            {
                throw new InvalidOperationException($"{package}@{head} failed verification (exit {verification.Run.ExitCode}): "
                    + string.Join("; ", verification.Run.Scenarios.Select(scenario => $"{scenario.Name}={(scenario.Passed ? "pass" : "FAIL " + scenario.Message)}")));
            }
            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    // The product host IS the reference boot; what is the product's own is its cookie and the
    // shipped content a product fixture turns back on.
    public static E2ETestBuilder<Projects.DigitalBrain_AppHost> Create(string modelApiKey = "fixture-key", Dictionary<string, string?>? privateConfiguration = null)
        => ReferenceBrain.Create(modelApiKey, privateConfiguration)
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.CookieName)), "intochat.session")
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.ProtectionApplicationName)), "IntoChat.v1")
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.ProtectionContainerName)), "intochat-protection-v1");
}
