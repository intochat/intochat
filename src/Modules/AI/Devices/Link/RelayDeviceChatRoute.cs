using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.Devices.Link;

// The OS side of device chat across the cloud/OS split (owner decision D5): the brain's install asks the cloud relay over HTTP,
// with the machine credential its device link holds, for the models its brain's other online devices offer, and streams a chat
// through one of them. The relay names the credential's brain; a route for any other brain answers nothing, so one brain never
// sees another's devices. This machine is never offered to itself.
internal sealed class RelayDeviceChatRoute(
    ICloudBrainRelayClient relay,
    MachineId thisMachine,
    TimeProvider clock) : IDeviceChatRoute
{
    // The model catalog asks on every listing; the relay's answer is kept this long per brain.
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    // The device link's HTTP client waits without limit (chat streams); a listing does not.
    internal static readonly TimeSpan AskFor = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, BrainDeviceModels[] Devices)> _cache = new(StringComparer.Ordinal);

    public async Task<ModelCard[]> Models(BrainId owner, CancellationToken ct)
    {
        var devices = await DevicesOf(owner, ct).ConfigureAwait(false);
        var cards = new List<ModelCard>();
        foreach (var device in devices)
        {
            foreach (var advertised in device.Models)
            {
                var id = new DeviceModelRef(device.DeviceName, new ModelId(advertised.Id)).ToModelId();
                if (!DeviceModelRef.TryParse(id, out _)) { continue; }
                cards.Add(new ModelCard(id, AiProvider.Device, advertised.Family, advertised.Name, Where.Device,
                    (advertised.Can & ~Abilities.Tools) | Abilities.Text, Tier.Balanced));
            }
        }
        return [.. cards];
    }

    public async Task<IChatClient?> Chat(BrainId owner, DeviceModelRef model, CancellationToken ct)
    {
        var devices = await DevicesOf(owner, ct).ConfigureAwait(false);
        var offered = devices.Any(device => string.Equals(device.DeviceName, model.Device, StringComparison.Ordinal)
            && device.Models.Any(advertised => string.Equals(advertised.Id, model.Model.Value, StringComparison.OrdinalIgnoreCase)));
        return offered ? new RelayChatClient(relay, owner, model) : null;
    }

    private async Task<BrainDeviceModels[]> DevicesOf(BrainId owner, CancellationToken ct)
    {
        // Neither a local device link nor hosted service-assertion relay auth: nothing to ask, and no call leaves this install.
        if (!await relay.IsConfiguredAsync(ct).ConfigureAwait(false)) { return []; }
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(owner.Value, out var cached) && now - cached.At < CacheFor) { return cached.Devices; }
        BrainDeviceModels[] devices;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(AskFor);
            var answer = await relay.BrainModelsAsync(owner.Value, bounded.Token).ConfigureAwait(false);
            devices = string.Equals(answer.Brain, owner.Value, StringComparison.Ordinal)
                ? [.. answer.Devices.Where(device => !string.Equals(device.MachineId, thisMachine.Value, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(device.DeviceName))]
                : [];
        }
        catch (Exception error) when (error is DeviceLinkRevokedException or DeviceUnavailableException or HttpRequestException or InvalidDataException or JsonException
            || (error is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Revoked, refused (another brain), unreachable, malformed or too slow: no device is offered.
            OsConsoleLog.For<RelayDeviceChatRoute>().LogSwallowed(error, $"listing the relay's devices for brain '{owner.Value}'");
            devices = [];
        }
        _cache[owner.Value] = (now, devices);
        return devices;
    }
}

// Text-only IChatClient over the relay's chat stream.
internal sealed class RelayChatClient(
    ICloudBrainRelayClient relay,
    BrainId owner,
    DeviceModelRef model) : IChatClient
{
    private readonly string _fullModelId = model.ToModelId().Value;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false)) { updates.Add(update); }
        var response = updates.ToChatResponse();
        response.ModelId ??= _fullModelId;
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var job = new DeviceChatJob(Guid.NewGuid().ToString("N"), model.Model.Value, WireMessages(messages, options?.Instructions),
            options?.Temperature, options?.MaxOutputTokens, Activity.Current?.Id);
        var chatRequest = new BrainChatRequest(owner.Value, model.Device, job);
        using var response = await relay.BrainChatAsync(chatRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { throw new DeviceUnavailableException($"relay-{(int)response.StatusCode}"); }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            DeviceChunk? chunk;
            try { chunk = JsonSerializer.Deserialize(line, DeviceWireJson.Default.DeviceChunk); }
            catch (JsonException) { throw new DeviceUnavailableException("relay-malformed"); }
            if (chunk is null) { continue; }
            if (!string.IsNullOrEmpty(chunk.Delta))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk.Delta) { ModelId = _fullModelId };
            }
            if (!string.IsNullOrEmpty(chunk.Error)) { throw new DeviceUnavailableException(chunk.Error); }
            if (chunk.Done)
            {
                var contents = new List<AIContent>(1);
                if (chunk.InputTokens is not null || chunk.OutputTokens is not null)
                {
                    var input = chunk.InputTokens ?? 0;
                    var output = chunk.OutputTokens ?? 0;
                    contents.Add(new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output }));
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, contents) { FinishReason = ChatFinishReason.Stop, ModelId = _fullModelId };
                yield break;
            }
        }
        throw new DeviceUnavailableException("relay-ended");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static DeviceMessage[] WireMessages(IEnumerable<ChatMessage> messages, string? instructions)
    {
        var list = new List<DeviceMessage>();
        if (!string.IsNullOrWhiteSpace(instructions)) { list.Add(new DeviceMessage("system", instructions)); }
        foreach (var message in messages)
        {
            if (string.IsNullOrEmpty(message.Text)) { continue; }
            var role = message.Role == ChatRole.System ? "system" : message.Role == ChatRole.Assistant ? "assistant" : "user";
            list.Add(new DeviceMessage(role, message.Text));
        }
        return [.. list];
    }
}
