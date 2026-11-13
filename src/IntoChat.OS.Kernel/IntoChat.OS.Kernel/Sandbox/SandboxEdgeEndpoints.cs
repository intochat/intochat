using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Kernel.Composition;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.Windows;
using IntoChat.OS.Modules.Notifications;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Sandbox;

/// <summary>
/// Kernel HTTP edge gateway handling capability RPCs, signals, and logs originating from
/// sandboxed apps running inside the hardened <c>IntoChat.OS.Sandbox</c> worker.
/// </summary>
internal static class SandboxEdgeEndpoints
{
    internal sealed record EdgeInvokeRequest(string Contract, string Method, string? Slot, string ArgsJson);

    internal sealed record EdgeInvokeResponse(string? ResultJson, string? Error = null);

    internal sealed record EdgeSignalRequest(string SignalName, string PayloadJson);

    internal sealed record EdgeLogRequest(string Message, DateTimeOffset TimestampUtc);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/edge/sandbox").AllowAnonymous();

        group.MapPost("/invoke", async (
            HttpRequest httpRequest,
            EdgeInvokeRequest request,
            [FromServices] AppTokenService tokens,
            [FromServices] SdkAiAdapter ai,
            [FromServices] SdkTablesAdapter tables,
            [FromServices] IServiceProvider services,
            CancellationToken ct) =>
        {
            if (!TryAuthenticate(httpRequest, tokens, out var identity))
            {
                return Results.Unauthorized();
            }

            try
            {
                var identityClient = services.GetService<IntoChat.OS.Connections.Platform.Identity.IIdentityClient>();
                var owner = identityClient is not null
                    ? await identityClient.OwnerOfBrain(identity.BrainId).ConfigureAwait(false)
                    : null;
                string? resultJson;
                using (InProcessCallerScope.EnterApp(identity.BrainId, identity.AppKey, owner?.AccountId))
                {
                    resultJson = await DispatchContractAsync(identity, request, ai, tables, services, ct).ConfigureAwait(false);
                }

                return Results.Ok(new EdgeInvokeResponse(resultJson));
            }
            catch (Exception error) when (error is UnauthorizedAccessException or DigitalBrain.Capabilities.Storage.Confinement.ConfinementDenied)
            {
                return Results.Json(new EdgeInvokeResponse(null, error.Message), statusCode: StatusCodes.Status403Forbidden);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
            {
                return Results.Json(new EdgeInvokeResponse(null, error.Message), statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or NotSupportedException)
            {
                return Results.Json(new EdgeInvokeResponse(null, error.Message), statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        group.MapPost("/signals", async (
            HttpRequest httpRequest,
            EdgeSignalRequest request,
            [FromServices] AppTokenService tokens,
            [FromServices] ISandboxRuntimeBridge bridge,
            CancellationToken ct) =>
        {
            if (!TryAuthenticate(httpRequest, tokens, out var identity))
            {
                return Results.Unauthorized();
            }

            await bridge.ForwardSignalAsync(identity.BrainId, request.SignalName, request.PayloadJson, ct).ConfigureAwait(false);
            return Results.Ok(new { forwarded = true, signal = request.SignalName });
        });

        group.MapPost("/logs", (
            HttpRequest httpRequest,
            EdgeLogRequest request,
            [FromServices] AppTokenService tokens,
            [FromServices] ILoggerFactory loggers) =>
        {
            if (!TryAuthenticate(httpRequest, tokens, out var identity))
            {
                return Results.Unauthorized();
            }

            var logger = loggers.CreateLogger("IntoChat.OS.Sandbox.AppLog");
            logger.LogInformation(
                "[{BrainId}/{AppKey}] ({TimestampUtc}) {Message}",
                identity.BrainId,
                identity.AppKey,
                request.TimestampUtc,
                request.Message);
            return Results.Ok(new { logged = true });
        });
    }

    private static bool TryAuthenticate(
        HttpRequest request,
        AppTokenService tokens,
        out VerifiedAppToken identity)
    {
        identity = null!;
        var token = request.Headers["X-App-Token"].FirstOrDefault();
        if (!tokens.TryVerify(token, out var verified) || verified is null)
        {
            return false;
        }

        var claimedBrain = request.Headers["X-Brain-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(claimedBrain)
            && !string.Equals(claimedBrain, verified.BrainId, StringComparison.Ordinal))
        {
            return false;
        }

        var claimedApp = request.Headers["X-App-Key"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(claimedApp)
            && !string.Equals(claimedApp, verified.AppKey, StringComparison.Ordinal))
        {
            return false;
        }

        identity = verified;
        return true;
    }

    private static async Task<string?> DispatchContractAsync(
        VerifiedAppToken identity,
        EdgeInvokeRequest request,
        SdkAiAdapter ai,
        SdkTablesAdapter tables,
        IServiceProvider services,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Method);

        using var argsDoc = string.IsNullOrWhiteSpace(request.ArgsJson)
            ? JsonDocument.Parse("[]")
            : JsonDocument.Parse(request.ArgsJson);
        if (argsDoc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Sandbox argsJson must be a JSON array.", nameof(request));
        }

        var args = argsDoc.RootElement;

        if (string.Equals(request.Contract, typeof(INotifications).FullName, StringComparison.Ordinal))
        {
            return await DispatchNotificationsAsync(identity.BrainId, request.Method, args, services, ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Contract, typeof(IFolder).FullName, StringComparison.Ordinal))
        {
            var slot = string.IsNullOrWhiteSpace(request.Slot) ? "folder" : request.Slot;
            return await DispatchFolderAsync(identity.BrainId, slot, request.Method, args, services, ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Contract, typeof(IAi).FullName, StringComparison.Ordinal))
        {
            return await DispatchAiAsync(identity.BrainId, request.Method, request.Slot, args, ai, ct).ConfigureAwait(false);
        }

        if (string.Equals(request.Contract, typeof(ITableHandle).FullName, StringComparison.Ordinal))
        {
            var tableName = !string.IsNullOrWhiteSpace(request.Slot)
                ? request.Slot
                : throw new ArgumentException("Table name slot is required for ITableHandle invocations.", nameof(request));
            return await DispatchTableAsync(identity.BrainId, identity.AppKey, tableName, request.Method, args, tables, ct).ConfigureAwait(false);
        }

        return await DispatchGenericSlotAsync(identity, request, args, services, ct).ConfigureAwait(false);
    }

    private static async Task<string?> DispatchNotificationsAsync(
        string brainId,
        string method,
        JsonElement args,
        IServiceProvider services,
        CancellationToken ct)
    {
        var inbox = SiloNeurons.Get<INotificationsInbox>(services, INotificationsInbox.Key(brainId));
        switch (method)
        {
            case nameof(INotifications.SendAsync):
                {
                    var req = ReadArg<NotificationRequest>(args, 0)
                        ?? throw new ArgumentException("NotificationRequest is required.");
                    var receipt = await inbox.SendAsync(req, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(receipt);
                }
            case nameof(INotifications.NotifyAsync):
                {
                    var title = ReadArg<string>(args, 0) ?? throw new ArgumentException("Notification title is required.");
                    var body = ReadArg<string>(args, 1) ?? string.Empty;
                    var priority = args.GetArrayLength() > 2 ? ReadArg<NotificationPriority>(args, 2) : NotificationPriority.Normal;
                    var receipt = await inbox.NotifyAsync(title, body, priority, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(receipt);
                }
            case nameof(INotifications.ListAsync):
                {
                    var limit = args.GetArrayLength() > 0 ? ReadArg<int>(args, 0) : 50;
                    var unreadOnly = args.GetArrayLength() > 1 && ReadArg<bool>(args, 1);
                    var items = await inbox.ListAsync(limit, unreadOnly, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(items);
                }
            case nameof(INotifications.MarkReadAsync):
                {
                    var notificationId = ReadArg<string>(args, 0) ?? throw new ArgumentException("NotificationId is required.");
                    await inbox.MarkReadAsync(notificationId, ct).WaitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(INotifications.MarkAllReadAsync):
                await inbox.MarkAllReadAsync(ct).WaitAsync(ct).ConfigureAwait(false);
                return null;
            default:
                throw new NotSupportedException($"Unsupported INotifications method '{method}'.");
        }
    }

    private static async Task<string?> DispatchFolderAsync(
        string brainId,
        string slot,
        string method,
        JsonElement args,
        IServiceProvider services,
        CancellationToken ct)
    {
        var gateway = SiloNeurons.Get<IWindowsFolderGateway>(services, IWindowsFolderGateway.Key(brainId));
        switch (method)
        {
            case nameof(IFolder.ListAsync):
                {
                    var relativePath = args.GetArrayLength() > 0 ? (ReadArg<string>(args, 0) ?? string.Empty) : string.Empty;
                    var entries = await gateway.ListAsync(slot, relativePath, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(entries);
                }
            case nameof(IFolder.ReadTextAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    var content = await gateway.ReadTextAsync(slot, relativePath, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(content);
                }
            case nameof(IFolder.ReadBytesAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    var bytes = await gateway.ReadBytesAsync(slot, relativePath, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(bytes);
                }
            case nameof(IFolder.WriteTextAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    var content = ReadArg<string>(args, 1) ?? string.Empty;
                    await gateway.WriteTextAsync(slot, relativePath, content, ct).WaitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(IFolder.WriteBytesAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    var content = ReadArg<byte[]>(args, 1) ?? [];
                    await gateway.WriteBytesAsync(slot, relativePath, content, ct).WaitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(IFolder.ExistsAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    var exists = await gateway.ExistsAsync(slot, relativePath, ct).WaitAsync(ct).ConfigureAwait(false);
                    return Serialize(exists);
                }
            case nameof(IFolder.DeleteAsync):
                {
                    var relativePath = ReadArg<string>(args, 0) ?? throw new ArgumentException("Relative path is required.");
                    await gateway.DeleteAsync(slot, relativePath, ct).WaitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(IFolder.MoveAsync):
                {
                    var src = ReadArg<string>(args, 0) ?? throw new ArgumentException("Source path is required.");
                    var dst = ReadArg<string>(args, 1) ?? throw new ArgumentException("Destination path is required.");
                    await gateway.MoveAsync(slot, src, dst, ct).WaitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            default:
                throw new NotSupportedException($"Unsupported IFolder method '{method}'.");
        }
    }

    private static async Task<string?> DispatchAiAsync(
        string brainId,
        string method,
        string? targetTypeHint,
        JsonElement args,
        SdkAiAdapter ai,
        CancellationToken ct)
    {
        switch (method)
        {
            case nameof(IAi.AskAsync):
                {
                    var prompt = ReadArg<string>(args, 0) ?? throw new ArgumentException("Prompt is required.");
                    var systemPrompt = args.GetArrayLength() > 1 ? ReadArg<string>(args, 1) : null;
                    var answer = await ai.AskAsync(brainId, prompt, systemPrompt, ct).ConfigureAwait(false);
                    return Serialize(answer);
                }
            case nameof(IAi.ExtractAsync):
                {
                    var prompt = ReadArg<string>(args, 0) ?? throw new ArgumentException("Prompt is required.");
                    return await ai.ExtractJsonAsync(brainId, prompt, targetTypeHint, ct).ConfigureAwait(false);
                }
            default:
                throw new NotSupportedException($"Unsupported IAi method '{method}'.");
        }
    }

    private static async Task<string?> DispatchTableAsync(
        string brainId,
        string appKey,
        string tableName,
        string method,
        JsonElement args,
        SdkTablesAdapter tables,
        CancellationToken ct)
    {
        switch (method)
        {
            case nameof(ITableHandle.EnsureAsync):
                {
                    var columns = ReadArg<TableColumnSpec[]>(args, 0) ?? [];
                    await tables.EnsureAsync(brainId, appKey, tableName, columns, ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(ITableHandle.UpsertAsync):
                {
                    var key = ReadArg<string>(args, 0) ?? throw new ArgumentException("Row key is required.");
                    var json = ReadArg<string>(args, 1) ?? "{}";
                    await tables.UpsertAsync(brainId, appKey, tableName, key, json, ct).ConfigureAwait(false);
                    return null;
                }
            case nameof(ITableHandle.GetAsync):
                {
                    var key = ReadArg<string>(args, 0) ?? throw new ArgumentException("Row key is required.");
                    var json = await tables.GetAsync(brainId, appKey, tableName, key, ct).ConfigureAwait(false);
                    return json is null ? null : Serialize(json);
                }
            case nameof(ITableHandle.ListAsync):
                {
                    var offset = args.GetArrayLength() > 0 ? ReadArg<int>(args, 0) : 0;
                    var limit = args.GetArrayLength() > 1 ? ReadArg<int>(args, 1) : 100;
                    var rows = await tables.ListAsync(brainId, appKey, tableName, offset, limit, ct).ConfigureAwait(false);
                    return Serialize(rows);
                }
            case nameof(ITableHandle.DeleteAsync):
                {
                    var key = ReadArg<string>(args, 0) ?? throw new ArgumentException("Row key is required.");
                    var deleted = await tables.DeleteAsync(brainId, appKey, tableName, key, ct).ConfigureAwait(false);
                    return Serialize(deleted);
                }
            default:
                throw new NotSupportedException($"Unsupported ITableHandle method '{method}'.");
        }
    }

    private static async Task<string?> DispatchGenericSlotAsync(
        VerifiedAppToken identity,
        EdgeInvokeRequest request,
        JsonElement args,
        IServiceProvider services,
        CancellationToken ct)
    {
        var contract = KernelInvocationSink.ContractOf(request.Contract)
            ?? throw new NotSupportedException($"Contract '{request.Contract}' is not registered in the kernel.");
        var neuronType = contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? throw new NotSupportedException($"Contract '{request.Contract}' does not declare [DefaultNeuronType] or [NeuronAlias].");

        var slotKey = string.IsNullOrWhiteSpace(request.Slot) ? "default" : request.Slot.Trim();
        var neuronId = new NeuronId(new BrainId(identity.BrainId), neuronType, slotKey);
        var proxy = SiloNeurons.Get(services, contract, neuronId);

        var method = contract.GetMethod(request.Method, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new NotSupportedException($"Method '{request.Contract}.{request.Method}' was not found.");

        var parameters = method.GetParameters();
        var invokeArgs = new object?[parameters.Length];
        var jsonIdx = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                invokeArgs[i] = ct;
                continue;
            }

            if (jsonIdx < args.GetArrayLength())
            {
                invokeArgs[i] = args[jsonIdx].Deserialize(param.ParameterType, JsonSerializerOptions.Web);
            }
            else if (param.HasDefaultValue)
            {
                invokeArgs[i] = param.DefaultValue;
            }
            else if (param.ParameterType.IsValueType)
            {
                invokeArgs[i] = Activator.CreateInstance(param.ParameterType);
            }

            jsonIdx++;
        }

        object? rawResult;
        try
        {
            rawResult = method.Invoke(proxy, invokeArgs);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is { } inner)
        {
            throw inner;
        }

        if (rawResult is Task task)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
            if (!method.ReturnType.IsGenericType)
            {
                return null;
            }

            var value = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
            return value is null ? null : JsonSerializer.Serialize(value, value.GetType(), JsonSerializerOptions.Web);
        }

        return rawResult is null ? null : JsonSerializer.Serialize(rawResult, rawResult.GetType(), JsonSerializerOptions.Web);
    }

    private static T? ReadArg<T>(JsonElement args, int index)
    {
        if (index >= args.GetArrayLength())
        {
            return default;
        }

        return args[index].Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
