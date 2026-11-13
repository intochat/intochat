using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

/// <summary>HTTP-backed <see cref="IBrain"/> implementation mediating sandbox app calls through the kernel edge gateway.</summary>
public sealed class IntoChatClient : IBrain
{
    internal static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    private static readonly MethodInfo InvokeAsyncMethod = typeof(IntoChatClient)
        .GetMethod(nameof(InvokeAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo InvokeValueTaskAsyncMethod = typeof(IntoChatClient)
        .GetMethod(nameof(InvokeValueTaskAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Uri _kernelBaseUri;
    private readonly string _appToken;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

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

        Notifications = new NotificationsProxy(this);
        Ai = new AiProxy(this);
        Tables = new TablesProxy(this);
    }

    public string BrainId { get; }
    public string AppKey { get; }
    public INotifications Notifications { get; }
    public IAi Ai { get; }
    public ITables Tables { get; }

    public IFolder Folder(string slot = "folder")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return new FolderProxy(this, slot);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "Sandbox worker executes dynamic user apps with full reflection.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Sandbox worker requires dynamic DispatchProxy generation.")]
    public TContract Slot<TContract>(string? slot = null) where TContract : class
    {
        if (!typeof(TContract).IsInterface)
        {
            throw new ArgumentException($"Slot contract '{typeof(TContract).FullName}' must be an interface.", nameof(TContract));
        }

        var proxy = DispatchProxy.Create<TContract, ContractDispatchProxy>();
        var dispatchProxy = (ContractDispatchProxy)(object)proxy;
        var contractName = typeof(TContract).FullName ?? typeof(TContract).Name;
        dispatchProxy.Configure(this, contractName, slot);
        return proxy;
    }

    public async Task PublishAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : DigitalBrain.Signal
    {
        ArgumentNullException.ThrowIfNull(signal);

        var signalName = typeof(TSignal).GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias
            ?? typeof(TSignal).Name;
        var payloadJson = JsonSerializer.Serialize(signal, typeof(TSignal), JsonOptions);
        var requestBody = new SandboxSignalRequest(signalName, payloadJson);

        using var response = await SendKernelPostAsync("/edge/sandbox/signals", requestBody, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/edge/sandbox/signals", cancellationToken).ConfigureAwait(false);
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

    internal async Task InvokeVoidAsync(
        string contract,
        string method,
        string? slot,
        object?[] args,
        CancellationToken cancellationToken)
    {
        _ = await SendInvokeAsync(contract, method, slot, args, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<TResult> InvokeAsync<TResult>(
        string contract,
        string method,
        string? slot,
        object?[] args,
        CancellationToken cancellationToken)
    {
        var resultJson = await SendInvokeAsync(contract, method, slot, args, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<TResult>(resultJson, JsonOptions)!;
    }

    internal async ValueTask<TResult> InvokeValueTaskAsync<TResult>(
        string contract,
        string method,
        string? slot,
        object?[] args,
        CancellationToken cancellationToken)
        => await InvokeAsync<TResult>(contract, method, slot, args, cancellationToken).ConfigureAwait(false);

    internal object InvokeProxyMethod(
        string contractName,
        string? slot,
        MethodInfo targetMethod,
        object?[]? args)
    {
        var parameters = targetMethod.GetParameters();
        var ct = CancellationToken.None;
        var filteredArgs = new List<object?>(parameters.Length);

        for (var i = 0; i < parameters.Length; i++)
        {
            var value = args is not null && i < args.Length ? args[i] : null;
            if (parameters[i].ParameterType == typeof(CancellationToken))
            {
                if (value is CancellationToken token)
                {
                    ct = token;
                }
                continue;
            }

            filteredArgs.Add(value);
        }

        var argsArray = filteredArgs.ToArray();
        var returnType = targetMethod.ReturnType;

        if (returnType == typeof(Task))
        {
            return InvokeVoidAsync(contractName, targetMethod.Name, slot, argsArray, ct);
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            return InvokeAsyncMethod.MakeGenericMethod(resultType)
                .Invoke(this, [contractName, targetMethod.Name, slot, argsArray, ct])!;
        }

        if (returnType == typeof(ValueTask))
        {
            return new ValueTask(InvokeVoidAsync(contractName, targetMethod.Name, slot, argsArray, ct));
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            return InvokeValueTaskAsyncMethod.MakeGenericMethod(resultType)
                .Invoke(this, [contractName, targetMethod.Name, slot, argsArray, ct])!;
        }

        throw new NotSupportedException(
            $"Slot contract method '{contractName}.{targetMethod.Name}' must return Task, Task<T>, ValueTask, or ValueTask<T>.");
    }

    private async Task<string?> SendInvokeAsync(
        string contract,
        string method,
        string? slot,
        object?[] args,
        CancellationToken cancellationToken)
    {
        var argsJson = JsonSerializer.Serialize(args, JsonOptions);
        var payload = new SandboxInvokeRequest(contract, method, slot, argsJson);

        using var response = await SendKernelPostAsync("/edge/sandbox/invoke", payload, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "/edge/sandbox/invoke", cancellationToken).ConfigureAwait(false);

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
                return resultJsonElement.ValueKind == JsonValueKind.String
                    ? resultJsonElement.GetString()
                    : resultJsonElement.ValueKind == JsonValueKind.Null
                        ? null
                        : resultJsonElement.GetRawText();
            }
        }

        return body;
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
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Kernel sandbox endpoint '{path}' failed with status {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}",
            null,
            response.StatusCode);
    }

    private sealed class NotificationsProxy(IntoChatClient client) : INotifications
    {
        private static readonly string ContractName = typeof(INotifications).FullName!;

        public Task<NotificationReceipt> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return client.InvokeAsync<NotificationReceipt>(ContractName, nameof(SendAsync), null, [request], cancellationToken);
        }

        public Task<NotificationReceipt> NotifyAsync(
            string title,
            string body,
            NotificationPriority priority = NotificationPriority.Normal,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(body);
            return client.InvokeAsync<NotificationReceipt>(ContractName, nameof(NotifyAsync), null, [title, body, priority], cancellationToken);
        }

        public Task<NotificationItem[]> ListAsync(int limit = 50, bool unreadOnly = false, CancellationToken cancellationToken = default)
            => client.InvokeAsync<NotificationItem[]>(ContractName, nameof(ListAsync), null, [limit, unreadOnly], cancellationToken);

        public Task MarkReadAsync(string notificationId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);
            return client.InvokeVoidAsync(ContractName, nameof(MarkReadAsync), null, [notificationId], cancellationToken);
        }

        public Task MarkAllReadAsync(CancellationToken cancellationToken = default)
            => client.InvokeVoidAsync(ContractName, nameof(MarkAllReadAsync), null, [], cancellationToken);
    }

    private sealed class AiProxy(IntoChatClient client) : IAi
    {
        private static readonly string ContractName = typeof(IAi).FullName!;

        public Task<string> AskAsync(string prompt, string? systemPrompt = null, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            return client.InvokeAsync<string>(ContractName, nameof(AskAsync), null, [prompt, systemPrompt], cancellationToken);
        }

        public Task<T> ExtractAsync<T>(string prompt, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            var targetTypeName = typeof(T).FullName ?? typeof(T).Name;
            return client.InvokeAsync<T>(ContractName, nameof(ExtractAsync), targetTypeName, [prompt], cancellationToken);
        }
    }

    private sealed class TablesProxy(IntoChatClient client) : ITables
    {
        public ITableHandle Table(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return new TableHandleProxy(client, name);
        }
    }

    private sealed class TableHandleProxy(IntoChatClient client, string tableName) : ITableHandle
    {
        private static readonly string ContractName = typeof(ITableHandle).FullName!;

        public Task EnsureAsync(TableColumnSpec[] columns, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(columns);
            return client.InvokeVoidAsync(ContractName, nameof(EnsureAsync), tableName, [columns], cancellationToken);
        }

        public Task UpsertAsync(string key, string jsonRow, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(jsonRow);
            return client.InvokeVoidAsync(ContractName, nameof(UpsertAsync), tableName, [key, jsonRow], cancellationToken);
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return client.InvokeAsync<string?>(ContractName, nameof(GetAsync), tableName, [key], cancellationToken);
        }

        public Task<TableRow[]> ListAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
            => client.InvokeAsync<TableRow[]>(ContractName, nameof(ListAsync), tableName, [offset, limit], cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return client.InvokeAsync<bool>(ContractName, nameof(DeleteAsync), tableName, [key], cancellationToken);
        }
    }

    private sealed class FolderProxy(IntoChatClient client, string slot) : IFolder
    {
        private static readonly string ContractName = typeof(IFolder).FullName!;

        public async Task<FolderEntry[]> ListAsync(string relativePath = "", CancellationToken cancellationToken = default)
            => await client.InvokeAsync<FolderEntry[]>(ContractName, nameof(ListAsync), slot, [relativePath], cancellationToken).ConfigureAwait(false)
                ?? [];

        public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            return client.InvokeAsync<string>(ContractName, nameof(ReadTextAsync), slot, [relativePath], cancellationToken);
        }

        public async Task<byte[]> ReadBytesAsync(string relativePath, CancellationToken cancellationToken = default)
            => await client.InvokeAsync<byte[]>(ContractName, nameof(ReadBytesAsync), slot, [relativePath], cancellationToken).ConfigureAwait(false)
                ?? [];

        public Task WriteTextAsync(string relativePath, string content, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentNullException.ThrowIfNull(content);
            return client.InvokeVoidAsync(ContractName, nameof(WriteTextAsync), slot, [relativePath, content], cancellationToken);
        }

        public Task WriteBytesAsync(string relativePath, byte[] content, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            ArgumentNullException.ThrowIfNull(content);
            return client.InvokeVoidAsync(ContractName, nameof(WriteBytesAsync), slot, [relativePath, content], cancellationToken);
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            return client.InvokeAsync<bool>(ContractName, nameof(ExistsAsync), slot, [relativePath], cancellationToken);
        }

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
            return client.InvokeVoidAsync(ContractName, nameof(DeleteAsync), slot, [relativePath], cancellationToken);
        }

        public Task MoveAsync(string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
            return client.InvokeVoidAsync(ContractName, nameof(MoveAsync), slot, [sourceRelativePath, destinationRelativePath], cancellationToken);
        }
    }
}

/// <summary>Dynamic proxy forwarding interface calls on <see cref="IBrain.Slot{TContract}"/> to <see cref="IntoChatClient"/>.</summary>
internal class ContractDispatchProxy : DispatchProxy
{
    private IntoChatClient? _client;
    private string? _contractName;
    private string? _slot;

    internal void Configure(IntoChatClient client, string contractName, string? slot)
    {
        _client = client;
        _contractName = contractName;
        _slot = slot;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (_client is null || _contractName is null)
        {
            throw new InvalidOperationException("ContractDispatchProxy has not been configured.");
        }

        return _client.InvokeProxyMethod(_contractName, _slot, targetMethod, args);
    }
}
