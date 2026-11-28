using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

internal sealed record NdjsonStreamRequest<TChunk, TResult>(
    string JobId,
    string StreamLabel,
    string FallbackLabel,
    ILogger Logger,
    JsonTypeInfo<TChunk> ChunkTypeInfo,
    Func<CancellationToken, IAsyncEnumerable<TChunk>> ProduceChunks,
    Func<CancellationToken, Task<TResult>> ExecuteUnaryFallback,
    Func<HttpContent, CancellationToken, Task<bool>> SendStream,
    Func<TResult, CancellationToken, Task> ReportFallbackResult,
    Func<TChunk, TChunk> StampChunk,
    Func<TResult, TResult> StampResult,
    Func<TChunk, TResult?> GetChunkResult,
    Func<TChunk, string?> GetChunkError,
    Func<TChunk, bool> IsChunkDone,
    Func<TResult, string?, TChunk> CreateDoneChunk,
    Func<string, TResult> CreateErrorResult,
    Func<string?, TResult> CreateSynthesizedDoneResult,
    Func<string?, TResult> CreateMissingFallbackResult,
    Func<string>? FormatTimeoutMessage = null,
    Func<TResult>? CreateTimeoutFallbackResult = null)
    where TResult : class;

// Generic NDJSON streaming engine with IAsyncEnumerator error/timeout isolation
// and a strict producerRan guard that guarantees non-double-execution on 404/501 unary fallback.
internal static class NdjsonJobStreamer
{
    public static async Task ExecuteAsync<TChunk, TResult>(
        NdjsonStreamRequest<TChunk, TResult> request,
        CancellationToken jobCt,
        CancellationToken outerCt)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(request);

        TResult? capturedResult = null;
        string? capturedError = null;
        var producerRan = false;

        using var content = new NdjsonPushContent<TChunk>(
            request.ChunkTypeInfo,
            async (write, token) =>
            {
                producerRan = true;
                var emittedDone = false;
                Exception? execError = null;
                IAsyncEnumerator<TChunk>? enumerator = null;
                using var streamExecCts = CancellationTokenSource.CreateLinkedTokenSource(token, jobCt);
                var execToken = streamExecCts.Token;
                try
                {
                    try
                    {
                        enumerator = request.ProduceChunks(execToken).GetAsyncEnumerator(execToken);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || outerCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        execError = new TimeoutException(request.FormatTimeoutMessage?.Invoke() ?? "Streaming job execution timed out.");
                    }
                    catch (Exception error)
                    {
                        request.Logger.LogWarning(error, "{StreamLabel} job {JobId} failed with {ErrorType}.", request.StreamLabel, request.JobId, error.GetType().Name);
                        execError = error;
                    }

                    while (execError is null && enumerator is not null)
                    {
                        TChunk rawChunk;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            rawChunk = enumerator.Current;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested || outerCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            execError = new TimeoutException(request.FormatTimeoutMessage?.Invoke() ?? "Streaming job execution timed out.");
                            break;
                        }
                        catch (Exception error)
                        {
                            request.Logger.LogWarning(error, "{StreamLabel} job {JobId} failed with {ErrorType}.", request.StreamLabel, request.JobId, error.GetType().Name);
                            execError = error;
                            break;
                        }

                        var chunk = request.StampChunk(rawChunk);
                        if (request.GetChunkResult(chunk) is { } result)
                        {
                            capturedResult = result;
                        }

                        var chunkError = request.GetChunkError(chunk);
                        if (!string.IsNullOrWhiteSpace(chunkError))
                        {
                            capturedError = chunkError;
                        }

                        if (request.IsChunkDone(chunk))
                        {
                            emittedDone = true;
                        }

                        // Isolated from executor catch so transport write() failures propagate directly.
                        await write(chunk, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (execError is not null)
                {
                    capturedResult = request.CreateErrorResult(execError.Message);
                    await write(request.CreateDoneChunk(capturedResult, execError.Message), token).ConfigureAwait(false);
                }
                else if (!emittedDone)
                {
                    capturedResult ??= request.CreateSynthesizedDoneResult(capturedError);
                    await write(request.CreateDoneChunk(capturedResult, null), token).ConfigureAwait(false);
                }
            });

        var streamed = await request.SendStream(content, outerCt).ConfigureAwait(false);
        if (streamed)
        {
            return;
        }

        // Backward compatibility: older relays return 404/501 for streaming endpoints.
        // Only execute via the unary path if the streaming producer never ran; never double-execute.
        TResult fallback;
        if (!producerRan)
        {
            try
            {
                fallback = request.StampResult(await request.ExecuteUnaryFallback(jobCt).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                fallback = request.CreateTimeoutFallbackResult is not null
                    ? request.CreateTimeoutFallbackResult()
                    : request.CreateErrorResult($"timeout: {request.FormatTimeoutMessage?.Invoke() ?? "Job execution timed out."}");
            }
            catch (Exception error)
            {
                request.Logger.LogWarning(error, "{FallbackLabel} job {JobId} fallback failed with {ErrorType}.", request.FallbackLabel, request.JobId, error.GetType().Name);
                fallback = request.CreateErrorResult(error.Message);
            }
        }
        else
        {
            fallback = capturedResult ?? request.CreateMissingFallbackResult(capturedError);
        }

        await request.ReportFallbackResult(fallback, outerCt).ConfigureAwait(false);
    }
}

internal sealed class NdjsonPushContent<T> : HttpContent
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<Func<T, CancellationToken, Task>, CancellationToken, Task> _producer;

    public NdjsonPushContent(
        JsonTypeInfo<T> typeInfo,
        Func<Func<T, CancellationToken, Task>, CancellationToken, Task> producer)
    {
        _typeInfo = typeInfo;
        _producer = producer;
        Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson") { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(stream, Utf8NoBom, bufferSize: 1024, leaveOpen: true);
        await _producer(async (chunk, ct) =>
        {
            var line = JsonSerializer.Serialize(chunk, _typeInfo);
            await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await writer.WriteAsync('\n').ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
