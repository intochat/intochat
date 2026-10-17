using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Monnet;

namespace DigitalBrain.Modules.Monnet.Tests;

// A brain whose deployment merchant is "m-deploy" on the fake.
internal sealed class MonnetBrain(ITestBrain brain, FakeMonnet fake) : IAsyncDisposable
{
    public const string DeploymentMerchant = "m-deploy";
    public const string WebhookSecret = "webhook-secret";

    public ITestBrain Brain => brain;
    public FakeMonnet Fake => fake;
    public Person Owner => brain.Owner;
    public string ConnectionKey => ConnectionKeys.For(Owner.BrainId, "monnet", "shop");
    public string DeploymentKey => ConnectionKeys.For(Owner.BrainId, "monnet", "deployment");

    public static async Task<MonnetBrain> StartAsync(CancellationToken ct, bool withWebhookSecret = true)
    {
        var fake = await FakeMonnet.StartAsync(ct);
        var builder = TestBrain.Create()
            .WithModule<MonnetModule, MonnetModuleOptions>(options => { options.BaseUrl = fake.Origin; options.AllowLoopback = true; })
            .WithIntegration(MonnetModule.Integration, "ApiKey", "deployment-key")
            .WithIntegration(MonnetModule.Integration, "MerchantId", DeploymentMerchant);
        if (withWebhookSecret) { builder = builder.WithIntegration(MonnetModule.Integration, "WebhookSecret", WebhookSecret); }
        return new(await builder.StartAsync(ct), fake);
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
        await brain.DisposeAsync();
        await fake.DisposeAsync();
    }
}
