using System.Collections;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Sandbox;

/// <summary>
/// Out-of-process NDJSON turn worker invoked via <c>IntoChat.OS.Sandbox --unit-host</c>.
/// Binds zero TCP sockets, holds zero host secrets, and executes compiled <c>[App]</c> assemblies over anonymous stdin/stdout pipes.
/// </summary>
internal static class UnitHostWorker
{
    public static async Task<int> RunAsync(Stream inputStream, Stream outputStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var httpClient = new HttpClient(new OfflineUnitHttpMessageHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri("http://sandbox.invalid/", UriKind.Absolute),
        };
        var host = new AppSandboxHost(httpClient, new Uri("http://sandbox.invalid/", UriKind.Absolute), NullLogger<AppSandboxHost>.Instance);

        using var reader = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        await using var writer = new StreamWriter(outputStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

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
                await writer.WriteLineAsync(JsonSerializer.Serialize(badFrame, UnitHostJson.Options)).ConfigureAwait(false);
                continue;
            }

            if (request is null)
            {
                continue;
            }

            if (string.Equals(request.Op, "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                var bye = new UnitHostFrameResponse(Id: request.Id, Ok: true, ProcessId: Environment.ProcessId);
                await writer.WriteLineAsync(JsonSerializer.Serialize(bye, UnitHostJson.Options)).ConfigureAwait(false);
                break;
            }

            var response = await HandleFrameAsync(host, request, cancellationToken).ConfigureAwait(false);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response, UnitHostJson.Options)).ConfigureAwait(false);
        }

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
            return new UnitHostFrameResponse(request.Id, Ok: false, Error: ex.Message, ProcessId: Environment.ProcessId);
        }
    }

    private sealed class OfflineUnitHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Out-of-process UnitHost turns do not permit direct outbound HTTP; side effects return OK acknowledgements or fail closed on slot invokes.
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (string.Equals(path, "/edge/sandbox/invoke", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"error\":\"Direct HTTP invoke disabled in pipe UnitHost mode.\"}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }
}
