using DigitalBrain.AI;
using DigitalBrain.AI.Models.OpenAI;
using DigitalBrain.Apps;
using DigitalBrain.ClickHouse;
using DigitalBrain.Flutter;
using DigitalBrain.Google.Gmail;
using DigitalBrain.Microsoft.GitHub;
using DigitalBrain.Postgres;
using DigitalBrain.Qdrant;
using DigitalBrain.Salesforce;
using DigitalBrain.Supabase;

namespace DigitalBrain.OS.Tests.E2E;

internal static class ProductE2E
{
    public static Task<ITestBrain> StartAsync(CancellationToken ct) => Create().StartAsync(ct);

    // The host ships its first-party apps in the background after it is healthy; a fact that opens
    // one has to wait until that app's bundle imported into the publisher's brain.
    public static async Task WaitUntilShippedAsync(ITestBrain brain, string appKey, CancellationToken ct, TimeSpan? budget = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget ?? TimeSpan.FromMinutes(4));
        while (true)
        {
            var snapshot = await brain.Get<IApp>(appKey).Read();
            if (snapshot.Status == AppStatus.Live) { return; }
            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    // The product host IS the reference boot; what is the product's own is its cookie and the
    // shipped content a product fixture turns back on.
    public static TestBrain Create(string modelApiKey = "fixture-key", Dictionary<string, string?>? privateConfiguration = null)
        => ReferenceBrain.Create(modelApiKey, privateConfiguration)
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.CookieName)), "intochat.session")
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.ProtectionApplicationName)), "IntoChat.v1")
            .WithHostConfiguration(Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(Platform.Identity.Configuration.IdentityHostOptions.ProtectionContainerName)), "intochat-protection-v1");
}
