using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// Webhook simulator double for publishing external OAuth / integration lifecycle facts into <see cref="TestOS"/>.
/// Dispatches brain-wide signals consumed by <c>ConnectionCatalogNeuron</c>.
/// </summary>
public static class WebhookSimulator
{
    [Neuron("integrations.webhook.emitter")]
    public sealed class WebhookEmitterNeuron : Neuron, IWebhookEmitter
    {
        public Task Connected(string connectorId, string connectionId, string authMethodId, string? account, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionConnected(connectorId, connectionId, authMethodId, account, probedAt));

        public Task Expired(string connectorId, string connectionId, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionExpired(connectorId, connectionId, probedAt));

        public Task Failing(string connectorId, string connectionId, string detail, DateTimeOffset? probedAt = null)
            => Publish(new ConnectionFailing(connectorId, connectionId, detail, probedAt));

        public Task Adopted(string connectorId, string connectionId, string authMethodId, string account)
            => Publish(new ConnectionAdopted(connectorId, connectionId, authMethodId, account));

        public Task Disconnected(string connectorId, string connectionId)
            => Publish(new ConnectionDisconnected(connectorId, connectionId));

        public Task Updated(string connectorId, string connectionId)
            => Publish(new ConnectionUpdated(connectorId, connectionId));
    }

    public interface IWebhookEmitter
    {
        Task Connected(string connectorId, string connectionId, string authMethodId, string? account, DateTimeOffset? probedAt = null);
        Task Expired(string connectorId, string connectionId, DateTimeOffset? probedAt = null);
        Task Failing(string connectorId, string connectionId, string detail, DateTimeOffset? probedAt = null);
        Task Adopted(string connectorId, string connectionId, string authMethodId, string account);
        Task Disconnected(string connectorId, string connectionId);
        Task Updated(string connectorId, string connectionId);
    }

    /// <summary>Registers the webhook emitter neuron type in the given TestOS instance.</summary>
    public static void Register(TestOS os)
    {
        os.Register<WebhookEmitterNeuron>();
    }

    private static IWebhookEmitter GetEmitter(TestOS os, BrainId brain)
    {
        Register(os);
        var emitterId = new NeuronId(brain, "integrations.webhook.emitter", "main");
        return os.Get<IWebhookEmitter>(emitterId);
    }

    /// <summary>Simulates an external OAuth connection established fact.</summary>
    public static async Task SimulateConnected(
        TestOS os,
        BrainId brain,
        string connectorId,
        string connectionId,
        string authMethodId = "oauth",
        string? account = "test-account",
        DateTimeOffset? probedAt = null)
    {
        var emitter = GetEmitter(os, brain);
        await emitter.Connected(connectorId, connectionId, authMethodId, account, probedAt);
        await os.Idle();
    }

    /// <summary>Simulates an OAuth token expiration fact.</summary>
    public static async Task SimulateExpired(
        TestOS os,
        BrainId brain,
        string connectorId,
        string connectionId,
        DateTimeOffset? probedAt = null)
    {
        var emitter = GetEmitter(os, brain);
        await emitter.Expired(connectorId, connectionId, probedAt);
        await os.Idle();
    }

    /// <summary>Simulates an external connection error / failure fact.</summary>
    public static async Task SimulateFailing(
        TestOS os,
        BrainId brain,
        string connectorId,
        string connectionId,
        string detail = "Connection timed out",
        DateTimeOffset? probedAt = null)
    {
        var emitter = GetEmitter(os, brain);
        await emitter.Failing(connectorId, connectionId, detail, probedAt);
        await os.Idle();
    }

    /// <summary>Simulates an adopted connection fact.</summary>
    public static async Task SimulateAdopted(
        TestOS os,
        BrainId brain,
        string connectorId,
        string connectionId,
        string authMethodId = "oauth",
        string account = "adopted-account")
    {
        var emitter = GetEmitter(os, brain);
        await emitter.Adopted(connectorId, connectionId, authMethodId, account);
        await os.Idle();
    }

    /// <summary>Simulates a connection disconnection / revocation fact.</summary>
    public static async Task SimulateDisconnected(
        TestOS os,
        BrainId brain,
        string connectorId,
        string connectionId)
    {
        var emitter = GetEmitter(os, brain);
        await emitter.Disconnected(connectorId, connectionId);
        await os.Idle();
    }
}
