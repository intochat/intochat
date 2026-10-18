using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Monnet;

namespace DigitalBrain.Modules.Monnet.Tests.Integration;

// A brain whose deployment merchant is "m-deploy" on the fake: its own, or a fresh person's on the shared host.
internal sealed class MonnetBrain(ITestBrain brain, FakeMonnet fake, Person? person = null) : IAsyncDisposable
{
    public const string DeploymentMerchant = "m-deploy";
    public const string WebhookSecret = "webhook-secret";

    public ITestBrain Brain => brain;
    public FakeMonnet Fake => fake;
    public Person Owner => person ?? brain.Owner;
    public string ConnectionKey => ConnectionKeys.For(Owner.BrainId, "monnet", "shop");
    public string DeploymentKey => ConnectionKeys.For(Owner.BrainId, "monnet", "deployment");

    public static async Task<MonnetBrain> StartAsync(CancellationToken ct, bool withWebhookSecret = true)
    {
        var fake = await FakeMonnet.StartAsync(ct);
        return new(await Compose(TestBrain.Create(), fake, withWebhookSecret).StartAsync(ct), fake);
    }

    public static async Task<MonnetBrain> ForPersonAsync(MonnetHost host, CancellationToken ct)
        => new(host.Brain, host.Fake, await host.PersonAsync(ct: ct));

    public static TestBrain Compose(TestBrain brain, FakeMonnet fake, bool withWebhookSecret = true)
    {
        var builder = brain
            .WithModule<MonnetModule, MonnetModuleOptions>(options => { options.BaseUrl = fake.Origin; options.AllowLoopback = true; })
            .WithIntegration(MonnetModule.Integration, "ApiKey", "deployment-key")
            .WithIntegration(MonnetModule.Integration, "MerchantId", DeploymentMerchant);
        if (withWebhookSecret) { builder = builder.WithIntegration(MonnetModule.Integration, "WebhookSecret", WebhookSecret); }
        return builder;
    }

    public async Task<IMonnet> ConnectAsync(string credentials)
    {
        using var caller = Owner.AsCaller();
        await brain.Get<IConnection>(ConnectionKey).Connect("credentials", credentials);
        return brain.Get<IMonnet>(ConnectionKey);
    }

    // The deployment's merchant, chosen by a member of the brain.
    public async Task<IMonnet> ConnectDeploymentAsync()
    {
        using var caller = Owner.AsCaller();
        await brain.Get<IConnection>(DeploymentKey).Connect("deployment", "default");
        return brain.Get<IMonnet>(DeploymentKey);
    }

    public async Task<HttpResponseMessage> DeliverAsync(object payload, CancellationToken ct, string? signature = null, string? brainId = null)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MonnetModule.WebhookPath}/{brainId ?? Owner.BrainId}") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(MonnetModule.SignatureHeader, signature ?? MonnetModule.Sign(WebhookSecret, body));
        return await brain.HttpClient.SendAsync(request, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (person is not null) { return; }
        await brain.DisposeAsync();
        await fake.DisposeAsync();
    }
}
