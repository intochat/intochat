using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Sandbox;

/// <summary>
/// Out-of-process NDJSON turn worker invoked via <c>IntoChat.OS.Sandbox --unit-host</c>.
/// Binds zero TCP sockets, holds zero host secrets, and executes compiled <c>[App]</c> or
/// <c>[State]/[Synapse]</c> assemblies over anonymous stdin/stdout pipes.
/// </summary>
internal static class UnitHostWorker
{
    public static async Task<int> RunAsync(
        Stream inputStream,
        Stream outputStream,
        string? contractsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var writeLock = new SemaphoreSlim(1, 1);
        using var forwardHttp = new HttpClient();
        var pendingCallbacks = new ConcurrentDictionary<string, TaskCompletionSource<UnitHostFrameRequest>>(StringComparer.Ordinal);

        using var reader = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(outputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        async Task WriteFrameAsync(UnitHostFrameResponse frame, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(frame, UnitHostJson.Options);
            await writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(json).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        async Task<UnitHostFrameRequest> SendPipeCallbackAsync(UnitHostFrameResponse callbackFrame, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<UnitHostFrameRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingCallbacks[callbackFrame.Id] = tcs;
            try
            {
                await WriteFrameAsync(callbackFrame, ct).ConfigureAwait(false);
                using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                pendingCallbacks.TryRemove(callbackFrame.Id, out _);
            }
        }

        using var httpClient = new HttpClient(new UnitHostHttpMessageHandler(SendPipeCallbackAsync, forwardHttp), disposeHandler: true)
        {
            BaseAddress = new Uri("http://sandbox.invalid/", UriKind.Absolute),
        };
        var host = new AppSandboxHost(
            httpClient,
            new Uri("http://sandbox.invalid/", UriKind.Absolute),
            SandboxConsoleLog.For<AppSandboxHost>(),
            contractsDirectory);

        var inFlight = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            UnitHostFrameRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<UnitHostFrameRequest>(line, UnitHostJson.Options);
            }
            catch (JsonException ex)
            {
                var badFrame = new UnitHostFrameResponse(Id: string.Empty, Ok: false, Error: $"Malformed JSON frame: {ex.Message}");
                await WriteFrameAsync(badFrame, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (request is null)
            {
                continue;
            }

            if (string.Equals(request.Op, "callback-reply", StringComparison.OrdinalIgnoreCase))
            {
                if (pendingCallbacks.TryRemove(request.Id, out var waiter))
                {
                    waiter.TrySetResult(request);
                }

                continue;
            }

            if (string.Equals(request.Op, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
                var bye = new UnitHostFrameResponse(Id: request.Id, Ok: true, ProcessId: Environment.ProcessId);
                await WriteFrameAsync(bye, cancellationToken).ConfigureAwait(false);
                break;
            }

            var workKey = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id;
            var task = Task.Run(async () =>
            {
                var response = await HandleFrameAsync(host, request, cancellationToken).ConfigureAwait(false);
                await WriteFrameAsync(response, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            inFlight[workKey] = task;
            _ = task.ContinueWith(_ => inFlight.TryRemove(workKey, out var removed), TaskScheduler.Default);
        }

        foreach (var waiter in pendingCallbacks.Values)
        {
            waiter.TrySetCanceled(cancellationToken);
        }

        await Task.WhenAll(inFlight.Values).ConfigureAwait(false);
        return 0;
    }

    private static async Task<UnitHostFrameResponse> HandleFrameAsync(
        AppSandboxHost host,
        UnitHostFrameRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Op.ToLowerInvariant())
            {
                case "ping":
                    return new UnitHostFrameResponse(
                        Id: request.Id,
                        Ok: true,
                        ProcessId: Environment.ProcessId);

                case "env":
                    {
                        var snapshot = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                        {
                            if (entry.Key is string k && entry.Value is string v)
                            {
                                snapshot[k] = v;
                            }
                        }

                        return new UnitHostFrameResponse(
                            Id: request.Id,
                            Ok: true,
                            EnvironmentSnapshot: snapshot,
                            ProcessId: Environment.ProcessId);
                    }

                case "deploy":
                    {
                        if (request.Deploy is null)
                        {
                            return new UnitHostFrameResponse(request.Id, Ok: false, Error: "Deploy payload is required.");
                        }

                        var deployed = await host.DeployAsync(request.Deploy, cancellationToken).ConfigureAwait(false);
                        return new UnitHostFrameResponse(request.Id, Ok: true, Deploy: deployed, ProcessId: Environment.ProcessId);
                    }

                case "dispatch":
                    {
                        if (request.Dispatch is null)
                        {
                            return new UnitHostFrameResponse(request.Id, Ok: false, Error: "Dispatch payload is required.");
                        }

                        var dispatched = await host.DispatchAsync(request.Dispatch, cancellationToken).ConfigureAwait(false);
                        return new UnitHostFrameResponse(request.Id, Ok: true, Dispatch: dispatched, ProcessId: Environment.ProcessId);
                    }

                case "unload":
                    {
                        if (request.Unload is null)
                        {
                            return new UnitHostFrameResponse(request.Id, Ok: false, Error: "Unload payload is required.");
                        }

                        var unloaded = await host.UnloadAsync(request.Unload.BrainId, request.Unload.AppKey, cancellationToken).ConfigureAwait(false);
                        return new UnitHostFrameResponse(request.Id, Ok: true, Unload: unloaded, ProcessId: Environment.ProcessId);
                    }

                default:
                    return new UnitHostFrameResponse(request.Id, Ok: false, Error: $"Unsupported UnitHost op '{request.Op}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SandboxConsoleLog.For(typeof(UnitHostWorker)).LogWarning(ex, "UnitHost request '{RequestId}' ({Op}) failed.", request.Id, request.Op);
            return new UnitHostFrameResponse(request.Id, Ok: false, Error: ex.Message, ProcessId: Environment.ProcessId);
        }
    }

    private sealed class UnitHostHttpMessageHandler(
        Func<UnitHostFrameResponse, CancellationToken, Task<UnitHostFrameRequest>> pipeCallback,
        HttpClient forwardClient) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            var path = uri?.AbsolutePath ?? string.Empty;

            if (uri is not null && string.Equals(uri.Scheme, "pipe", StringComparison.OrdinalIgnoreCase))
            {
                return await SendViaPipeAsync(request, path, cancellationToken).ConfigureAwait(false);
            }

            if (uri is not null
                && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                && !string.Equals(uri.Host, "sandbox.invalid", StringComparison.OrdinalIgnoreCase))
            {
                using var clone = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
                return await forwardClient.SendAsync(clone, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(path, "/edge/sandbox/invoke", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":\"Direct HTTP invoke disabled in offline UnitHost mode.\"}", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        private async Task<HttpResponseMessage> SendViaPipeAsync(HttpRequestMessage request, string path, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var appToken = HeaderValue(request, "X-App-Token");
            var brainId = HeaderValue(request, "X-Brain-Id");
            var appKey = HeaderValue(request, "X-App-Key");
            var callbackId = "cb-" + Guid.NewGuid().ToString("N");

            UnitHostFrameResponse frame;
            if (string.Equals(path, "/edge/sandbox/invoke", StringComparison.OrdinalIgnoreCase))
            {
                var invoke = JsonSerializer.Deserialize<SandboxInvokeRequest>(body, UnitHostJson.Options);
                frame = new UnitHostFrameResponse(
                    Id: callbackId,
                    Ok: true,
                    CallbackKind: "invoke",
                    BrainId: brainId,
                    AppKey: appKey,
                    AppToken: appToken,
                    Invoke: invoke);
            }
            else if (string.Equals(path, "/edge/sandbox/signals", StringComparison.OrdinalIgnoreCase))
            {
                var signal = JsonSerializer.Deserialize<SandboxSignalRequest>(body, UnitHostJson.Options);
                frame = new UnitHostFrameResponse(
                    Id: callbackId,
                    Ok: true,
                    CallbackKind: "signal",
                    BrainId: brainId,
                    AppKey: appKey,
                    AppToken: appToken,
                    Signal: signal);
            }
            else if (string.Equals(path, "/edge/sandbox/logs", StringComparison.OrdinalIgnoreCase))
            {
                var log = JsonSerializer.Deserialize<SandboxLogRequest>(body, UnitHostJson.Options);
                frame = new UnitHostFrameResponse(
                    Id: callbackId,
                    Ok: true,
                    CallbackKind: "log",
                    BrainId: brainId,
                    AppKey: appKey,
                    AppToken: appToken,
                    Log: log);
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var reply = await pipeCallback(frame, cancellationToken).ConfigureAwait(false);
            var statusCode = reply.StatusCode is > 0 ? (HttpStatusCode)reply.StatusCode.Value : (reply.Ok == true ? HttpStatusCode.OK : HttpStatusCode.InternalServerError);
            var responseBody = reply.InvokeResult is not null
                ? JsonSerializer.Serialize(reply.InvokeResult, UnitHostJson.Options)
                : JsonSerializer.Serialize(new SandboxInvokeResponse(null, reply.Error), UnitHostJson.Options);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }

        private static string? HeaderValue(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            foreach (var header in source.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (source.Content is not null)
            {
                var bytes = await source.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var content = new ByteArrayContent(bytes);
                foreach (var header in source.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                clone.Content = content;
            }

            return clone;
        }
    }
}
