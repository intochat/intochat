using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Modules.Devices.Link;

namespace IntoChat.OS.Modules.Devices.Tests.Unit.Link;

public sealed class NdjsonJobStreamerFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FallbackRunsUnaryOnceWhenStreamReturnsFalseBeforeProducerRuns()
    {
        var producerCalls = 0;
        var unaryCalls = 0;
        ConnectedSandboxResponse? reported = null;

        await NdjsonJobStreamer.ExecuteAsync(
            CreateSandboxRequest(
                produceChunks: async ct =>
                {
                    producerCalls++;
                    await Task.Yield();
                    return [];
                },
                executeUnary: _ =>
                {
                    unaryCalls++;
                    return Task.FromResult(new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: "{\"ok\":true}",
                        UpdatedStateJson: null,
                        Error: null,
                        ExecutedByMachineId: null));
                },
                sendStream: (_, _) => Task.FromResult(false),
                reportFallback: (res, _) =>
                {
                    reported = res;
                    return Task.CompletedTask;
                }),
            jobCt: Ct,
            outerCt: Ct);

        Assert.Equal(0, producerCalls);
        Assert.Equal(1, unaryCalls);
        Assert.NotNull(reported);
        Assert.True(reported!.Handled);
        Assert.Equal("pc-test-1", reported.ExecutedByMachineId);
    }

    [Fact]
    public async Task FallbackNeverDoubleExecutesWhenProducerAlreadyRan()
    {
        var producerCalls = 0;
        var unaryCalls = 0;
        ConnectedSandboxResponse? reported = null;

        await NdjsonJobStreamer.ExecuteAsync(
            CreateSandboxRequest(
                produceChunks: _ =>
                {
                    producerCalls++;
                    return Task.FromResult<SandboxStreamChunk[]>([
                        new SandboxStreamChunk(
                            Done: true,
                            Result: new ConnectedSandboxResponse(
                                Handled: true,
                                ResultJson: "{\"v\":42}",
                                UpdatedStateJson: null,
                                Error: null,
                                ExecutedByMachineId: null)),
                    ]);
                },
                executeUnary: _ =>
                {
                    unaryCalls++;
                    throw new InvalidOperationException("Unary fallback must not run after streaming producer ran.");
                },
                sendStream: async (content, ct) =>
                {
                    // Drain content (triggers producerRan = true), then simulate 404/501 response.
                    _ = await content.ReadAsStringAsync(ct);
                    return false;
                },
                reportFallback: (res, _) =>
                {
                    reported = res;
                    return Task.CompletedTask;
                }),
            jobCt: Ct,
            outerCt: Ct);

        Assert.Equal(1, producerCalls);
        Assert.Equal(0, unaryCalls);
        Assert.NotNull(reported);
        Assert.True(reported!.Handled);
        Assert.Equal("{\"v\":42}", reported.ResultJson);
        Assert.Equal("pc-test-1", reported.ExecutedByMachineId);
    }

    [Fact]
    public async Task JobTimeoutEmitsTerminalDoneErrorChunkWithoutThrowing()
    {
        var streamedChunks = new List<SandboxStreamChunk>();
        using var jobTimeoutCts = new CancellationTokenSource();

        await NdjsonJobStreamer.ExecuteAsync(
            CreateSandboxRequest(
                produceChunks: null,
                produceAsync: SlowHangingProducerAsync(jobTimeoutCts),
                executeUnary: _ => throw new InvalidOperationException("Should not fall back."),
                sendStream: async (content, ct) =>
                {
                    streamedChunks.AddRange(await ReadChunksAsync(content, ct));
                    return true;
                },
                reportFallback: (_, _) => Task.CompletedTask,
                formatTimeoutMessage: () => "Job exceeded 15s cap."),
            jobCt: jobTimeoutCts.Token,
            outerCt: Ct);

        var terminal = Assert.Single(streamedChunks);
        Assert.True(terminal.Done);
        Assert.Equal("Job exceeded 15s cap.", terminal.Error);
        Assert.NotNull(terminal.Result);
        Assert.False(terminal.Result!.Handled);
        Assert.Equal("pc-test-1", terminal.Result.ExecutedByMachineId);
    }

    [Fact]
    public async Task OuterCancellationPropagatesOperationCanceledException()
    {
        using var outerCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await NdjsonJobStreamer.ExecuteAsync(
                CreateSandboxRequest(
                    produceChunks: null,
                    produceAsync: CancellingProducerAsync(outerCts),
                    executeUnary: _ => throw new InvalidOperationException("Should not fall back."),
                    sendStream: async (content, ct) =>
                    {
                        _ = await content.ReadAsStringAsync(ct);
                        return true;
                    },
                    reportFallback: (_, _) => Task.CompletedTask),
                jobCt: outerCts.Token,
                outerCt: outerCts.Token);
        });
    }

    [Fact]
    public async Task SynthesizesTerminalDoneChunkAndStampsMachineIdWhenProducerOmitsDone()
    {
        var streamedChunks = new List<SandboxStreamChunk>();

        await NdjsonJobStreamer.ExecuteAsync(
            CreateSandboxRequest(
                produceChunks: _ => Task.FromResult<SandboxStreamChunk[]>([
                    new SandboxStreamChunk(Stdout: "hello\n", Done: false),
                ]),
                executeUnary: _ => throw new InvalidOperationException("Should not fall back."),
                sendStream: async (content, ct) =>
                {
                    streamedChunks.AddRange(await ReadChunksAsync(content, ct));
                    return true;
                },
                reportFallback: (_, _) => Task.CompletedTask),
            jobCt: Ct,
            outerCt: Ct);

        Assert.Equal(2, streamedChunks.Count);
        Assert.Equal("hello\n", streamedChunks[0].Stdout);
        Assert.False(streamedChunks[0].Done);
        Assert.True(streamedChunks[1].Done);
        Assert.NotNull(streamedChunks[1].Result);
        Assert.Equal("pc-test-1", streamedChunks[1].Result!.ExecutedByMachineId);
    }

    [Fact]
    public async Task ProducerExceptionEmitsTerminalDoneErrorChunkAndInvokesCallback()
    {
        var streamedChunks = new List<SandboxStreamChunk>();
        Exception? logged = null;

        await NdjsonJobStreamer.ExecuteAsync(
            CreateSandboxRequest(
                produceChunks: null,
                produceAsync: ThrowingProducerAsync(),
                executeUnary: _ => throw new InvalidOperationException("Should not fall back."),
                sendStream: async (content, ct) =>
                {
                    streamedChunks.AddRange(await ReadChunksAsync(content, ct));
                    return true;
                },
                reportFallback: (_, _) => Task.CompletedTask,
                onStreamError: err => logged = err),
            jobCt: Ct,
            outerCt: Ct);

        Assert.IsType<InvalidOperationException>(logged);
        Assert.Equal(2, streamedChunks.Count);
        Assert.True(streamedChunks[1].Done);
        Assert.Equal("boom-from-producer", streamedChunks[1].Error);
        Assert.Equal("pc-test-1", streamedChunks[1].Result!.ExecutedByMachineId);
    }

    private static NdjsonStreamRequest<SandboxStreamChunk, ConnectedSandboxResponse> CreateSandboxRequest(
        Func<CancellationToken, Task<SandboxStreamChunk[]>>? produceChunks,
        Func<CancellationToken, Task<ConnectedSandboxResponse>> executeUnary,
        Func<HttpContent, CancellationToken, Task<bool>> sendStream,
        Func<ConnectedSandboxResponse, CancellationToken, Task> reportFallback,
        Func<CancellationToken, IAsyncEnumerable<SandboxStreamChunk>>? produceAsync = null,
        Func<string>? formatTimeoutMessage = null,
        Action<Exception>? onStreamError = null)
    {
        const string machineId = "pc-test-1";
        return new NdjsonStreamRequest<SandboxStreamChunk, ConnectedSandboxResponse>(
            ChunkTypeInfo: DeviceWireJson.Default.SandboxStreamChunk,
            ProduceChunks: produceAsync ?? (ct => YieldFromArrayAsync(produceChunks!, ct)),
            ExecuteUnaryFallback: executeUnary,
            SendStream: sendStream,
            ReportFallbackResult: reportFallback,
            StampChunk: chunk => chunk.Result is null || !string.IsNullOrWhiteSpace(chunk.Result.ExecutedByMachineId)
                ? chunk
                : chunk with { Result = chunk.Result with { ExecutedByMachineId = machineId } },
            StampResult: res => string.IsNullOrWhiteSpace(res.ExecutedByMachineId)
                ? res with { ExecutedByMachineId = machineId }
                : res,
            GetChunkResult: static c => c.Result,
            GetChunkError: static c => c.Error,
            IsChunkDone: static c => c.Done,
            CreateDoneChunk: static (res, err) => new SandboxStreamChunk(Done: true, Error: err, Result: res),
            CreateErrorResult: err => new ConnectedSandboxResponse(false, null, null, err, machineId),
            CreateSynthesizedDoneResult: _ => new ConnectedSandboxResponse(true, null, null, null, machineId),
            CreateMissingFallbackResult: err => new ConnectedSandboxResponse(false, null, null, err ?? "missing", machineId),
            FormatTimeoutMessage: formatTimeoutMessage,
            OnStreamError: onStreamError);
    }

    private static async IAsyncEnumerable<SandboxStreamChunk> YieldFromArrayAsync(
        Func<CancellationToken, Task<SandboxStreamChunk[]>> factory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var items = await factory(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            yield return item;
        }
    }

    private static Func<CancellationToken, IAsyncEnumerable<SandboxStreamChunk>> SlowHangingProducerAsync(CancellationTokenSource timeoutCts)
        => ct => Impl(timeoutCts, ct);

    private static async IAsyncEnumerable<SandboxStreamChunk> Impl(
        CancellationTokenSource timeoutCts,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await timeoutCts.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        yield break;
    }

    private static Func<CancellationToken, IAsyncEnumerable<SandboxStreamChunk>> CancellingProducerAsync(CancellationTokenSource outerCts)
        => ct => Impl(outerCts, ct);

    private static Func<CancellationToken, IAsyncEnumerable<SandboxStreamChunk>> ThrowingProducerAsync()
        => _ => ThrowImpl();

    private static async IAsyncEnumerable<SandboxStreamChunk> ThrowImpl()
    {
        await Task.Yield();
        yield return new SandboxStreamChunk(Stdout: "partial", Done: false);
        throw new InvalidOperationException("boom-from-producer");
    }

    private static async Task<List<SandboxStreamChunk>> ReadChunksAsync(HttpContent content, CancellationToken ct)
    {
        var body = await content.ReadAsStringAsync(ct);
        var chunks = new List<SandboxStreamChunk>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var chunk = JsonSerializer.Deserialize(line, DeviceWireJson.Default.SandboxStreamChunk);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }
        return chunks;
    }
}
