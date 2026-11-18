using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Log;
using IntoChat.Sdk;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Sandbox;

/// <summary>JSON payload sent to the kernel edge gateway for sandbox capability invocations.</summary>
public sealed record SandboxInvokeRequest(
    string Contract,
    string Method,
    string? Slot,
    string ArgsJson);

/// <summary>JSON envelope returned by the kernel edge gateway for sandbox capability invocations.</summary>
public sealed record SandboxInvokeResponse(
    string? ResultJson,
    string? Error = null);

/// <summary>JSON payload sent to the kernel edge gateway when an app publishes a signal.</summary>
public sealed record SandboxSignalRequest(
    string SignalName,
    string PayloadJson);

/// <summary>JSON payload sent to the kernel edge gateway when an app logs a trace line.</summary>
public sealed record SandboxLogRequest(
    string Message,
    DateTimeOffset TimestampUtc);

/// <summary>
/// <see cref="ISandboxBrain"/> implementation backed by DigitalBrain <see cref="NeuronProxy"/> and a single
/// <see cref="IInvocationSink"/> mediating sandbox capability calls through the kernel edge gateway.
/// </summary>
public sealed class IntoChatClient : ISandboxBrain
{
    internal const string DefaultSlotKey = "slot.default";
    internal static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private readonly Uri _kernelBaseUri;
    private readonly string _appToken;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly BrainId _neuronBrainId;
    private readonly SandboxInvocationSink _sink;

    public IntoChatClient(
        string brainId,
        string appKey,
        Uri kernelBaseUri,
        string appToken,
        HttpClient httpClient,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        ArgumentNullException.ThrowIfNull(kernelBaseUri);
        ArgumentNullException.ThrowIfNull(httpClient);

        BrainId = brainId;
        AppKey = appKey;
        _kernelBaseUri = kernelBaseUri;
        _appToken = appToken ?? string.Empty;
        _httpClient = httpClient;
        _logger = logger;
        _neuronBrainId = DigitalBrain.BrainId.IsValid(brainId) ? new BrainId(brainId) : DigitalBrain.BrainId.Kernel;
        _sink = new SandboxInvocationSink(this);

        Notifications = NeuronProxy.Create<INotifications>(new NeuronId(_neuronBrainId, "notifications", DefaultSlotKey), _sink);
        Ai = new AiProxy(_sink, _neuronBrainId);
        Tables = new TablesProxy(_sink, _neuronBrainId);
    }

    public string BrainId { get; }
    public string AppKey { get; }
    public INotifications Notifications { get; }
    public IAi Ai { get; }
    public ITables Tables { get; }

    public IFolder Folder(string slot = "folder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return NeuronProxy.Create<IFolder>(new NeuronId(_neuronBrainId, "folder", slot.Trim()), _sink);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Sandbox worker executes dynamic user apps with full reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Sandbox worker requires dynamic DispatchProxy generation.")]
    public TContract Get<TContract>(string key) where TContract : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return (TContract)Slot(typeof(TContract), key);
    }

    public TContract Slot<TContract>(string? slot = null) where TContract : class
        => (TContract)Slot(typeof(TContract), slot);

    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Sandbox worker binds runtime synapse slot interfaces dynamically.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Sandbox worker requires dynamic DispatchProxy generation.")]
    internal object Slot(Type contractType, string? slot = null)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        if (!contractType.IsInterface)
        {
            throw new ArgumentException($"Slot contract '{contractType.FullName}' must be an interface.", nameof(contractType));
        }

        var neuronType = ResolveNeuronType(contractType);
        var slotKey = string.IsNullOrWhiteSpace(slot) ? DefaultSlotKey : slot.Trim();
        var targetId = new NeuronId(_neuronBrainId, neuronType, slotKey);
        return NeuronProxy.Create(contractType, targetId, _sink);
    }

    public async Task PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);

        var signalName = typeof(TSignal).GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias
            ?? typeof(TSignal).Name;
        var payloadJson = JsonSerializer.Serialize(signal, typeof(TSignal), JsonOptions);
        var requestBody = new SandboxSignalRequest(signalName, payloadJson);

        using var response = await SendKernelPostAsync("/edge/sandbox/signals", requestBody, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/edge/sandbox/signals", cancellationToken, _logger).ConfigureAwait(false);
    }

    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _logger?.LogInformation("Sandbox app {BrainId}/{AppKey}: {Message}", BrainId, AppKey, message);

        try
        {
            var payload = new SandboxLogRequest(message, DateTimeOffset.UtcNow);
            using var response = await SendKernelPostAsync("/edge/sandbox/logs", payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug(
                    "Kernel trace endpoint returned {StatusCode} for {BrainId}/{AppKey}.",
                    (int)response.StatusCode,
                    BrainId,
                    AppKey);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogDebug(ex, "Optional kernel trace endpoint unavailable for {BrainId}/{AppKey}.", BrainId, AppKey);
        }
    }

    private static string ResolveNeuronType(Type contractType)
    {
        var candidate = contractType.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contractType.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias;
        if (!string.IsNullOrWhiteSpace(candidate)
            && !candidate.Contains('/', StringComparison.Ordinal)
            && !candidate.Any(char.IsControl))
        {
            return candidate!;
        }

        return "slot";
    }

    private Task<HttpResponseMessage> SendKernelPostAsync<TPayload>(
        string relativePath,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(_kernelBaseUri.ToString().TrimEnd('/') + relativePath, UriKind.Absolute);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(_appToken))
        {
            request.Headers.TryAddWithoutValidation("X-App-Token", _appToken);
        }

        request.Headers.TryAddWithoutValidation("X-Brain-Id", BrainId);
        request.Headers.TryAddWithoutValidation("X-App-Key", AppKey);

        return _httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string path,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var errorMessage = ExtractErrorDetail(detail, logger);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException(errorMessage ?? $"Access denied by kernel sandbox endpoint '{path}'.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException(errorMessage ?? $"Resource not found at kernel sandbox endpoint '{path}'.");
        }

        throw new HttpRequestException(
            $"Kernel sandbox endpoint '{path}' failed with status {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}",
            null,
            response.StatusCode);
    }

    private static string? ExtractErrorDetail(string body, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        if (!body.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return body;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String)
            {
                return errorElement.GetString();
            }
        }
        catch (JsonException error)
        {
            var effectiveLogger = logger is not null && logger.IsEnabled(LogLevel.Warning)
                ? logger
                : SandboxConsoleLog.For<IntoChatClient>();
            effectiveLogger.LogSwallowed(error, "Parsing sandbox invoke error payload.");
        }

        return body;
    }

    internal sealed class SandboxInvocationSink(IntoChatClient client) : IInvocationSink
    {
        public async Task<JsonElement?> Invoke(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            var slot = string.Equals(target.Key, DefaultSlotKey, StringComparison.Ordinal) ? null : target.Key;
            var argsJson = JsonSerializer.Serialize(call.Arguments, JsonOptions);
            var payload = new SandboxInvokeRequest(call.Contract, call.Method, slot, argsJson);

            using var response = await client.SendKernelPostAsync("/edge/sandbox/invoke", payload, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "/edge/sandbox/invoke", cancellationToken, client._logger).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(errorElement.GetString()))
                {
                    throw new InvalidOperationException(errorElement.GetString());
                }

                if (doc.RootElement.TryGetProperty("resultJson", out var resultJsonElement))
                {
                    var resultJson = resultJsonElement.ValueKind == JsonValueKind.String
                        ? resultJsonElement.GetString()
                        : resultJsonElement.ValueKind == JsonValueKind.Null
                            ? null
                            : resultJsonElement.GetRawText();

                    if (string.IsNullOrWhiteSpace(resultJson))
                    {
                        return null;
                    }

                    using var resultDoc = JsonDocument.Parse(resultJson);
                    return resultDoc.RootElement.Clone();
                }
            }

            return doc.RootElement.Clone();
        }

        public Task Post(NeuronId target, RuntimeInvocation call)
            => Invoke(target, call, CancellationToken.None);

        public async IAsyncEnumerable<JsonElement> Stream(
            NeuronId target,
            RuntimeInvocation call,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            throw new NotSupportedException("Streaming neuron calls are not supported over the sandbox edge.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class AiProxy(SandboxInvocationSink sink, BrainId brainId) : IAi
    {
        private static readonly string ContractName = typeof(IAi).FullName!;
        private readonly BrainId _brainId = brainId;
        private readonly NeuronId _defaultTarget = new(brainId, "ai", DefaultSlotKey);

        public async Task<string> AskAsync(string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            var call = new RuntimeInvocation(
                Behavior: "sandbox",
                Contract: ContractName,
                Key: DefaultSlotKey,
                Method: nameof(AskAsync),
                Arguments:
                [
                    JsonSerializer.SerializeToElement(prompt, JsonOptions),
                    JsonSerializer.SerializeToElement(systemPrompt, JsonOptions),
                ]);

            var result = await sink.Invoke(_defaultTarget, call, cancellationToken).ConfigureAwait(false);
            return result is { } json ? json.Deserialize<string>(JsonOptions) ?? string.Empty : string.Empty;
        }

        public async Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            var targetTypeName = typeof(T).FullName ?? typeof(T).Name;
            var target = new NeuronId(_brainId, "ai", targetTypeName);
            var call = new RuntimeInvocation(
                Behavior: "sandbox",
                Contract: ContractName,
                Key: targetTypeName,
                Method: nameof(ExtractAsync),
                Arguments: [JsonSerializer.SerializeToElement(prompt, JsonOptions)]);

            var result = await sink.Invoke(target, call, cancellationToken).ConfigureAwait(false);
            return result is { } json ? json.Deserialize<T>(JsonOptions)! : default!;
        }
    }

    private sealed class TablesProxy(SandboxInvocationSink sink, BrainId brainId) : ITables
    {
        public ITableHandle Table(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return NeuronProxy.Create<ITableHandle>(new NeuronId(brainId, "table", name), sink);
        }
    }
}
