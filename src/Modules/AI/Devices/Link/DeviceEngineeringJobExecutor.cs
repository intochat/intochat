using DigitalBrain.Identity.Local.Machine;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

// Executes Plane 2 connected engineering jobs with strict fail-closed AllowConnectedEngineering isolation
// (never falling back to AllowClientCodeSandbox) and NDJSON streaming + unary fallback.
internal sealed class DeviceEngineeringJobExecutor(
    CloudClient cloud,
    ILocalEngineeringExecutor localEngineeringExecutor,
    DeviceCapabilityReporter capabilities,
    MachineId thisMachine,
    TimeProvider clock,
    ILogger logger)
{
    public async Task ExecuteAsync(ConnectedEngineeringRequest job, CancellationToken ct)
    {
        // Fail-closed: strictly requires local AllowConnectedEngineering == true; NEVER falls back to AllowClientCodeSandbox.
        if (!capabilities.EffectiveAllowConnectedEngineering())
        {
            logger.LogInformation(
                "Refused Plane 2 connected engineering job {JobId} (run={RunId}): AllowConnectedEngineering is disabled on this PC.",
                job.JobId,
                job.RunId);
            await cloud.ReportEngineeringResultAsync(
                job.JobId,
                CreateErrorResponse(job.RunId, "consent-disabled: AllowConnectedEngineering is disabled on this PC."),
                ct).ConfigureAwait(false);
            return;
        }

        var timeoutSeconds = Math.Clamp(job.TimeoutSeconds, 15, 900);
        var executionTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(executionTimeout, clock);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var jobCt = jobCts.Token;

        if (localEngineeringExecutor is IStreamingLocalEngineeringExecutor streamingExecutor)
        {
            await NdjsonJobStreamer.ExecuteAsync(
                new NdjsonStreamRequest<EngineeringStreamChunk, ConnectedEngineeringResponse>(
                    JobId: job.JobId,
                    StreamLabel: "Streaming Plane 2 engineering",
                    FallbackLabel: "Local Plane 2 engineering",
                    Logger: logger,
                    ChunkTypeInfo: DeviceWireJson.Default.EngineeringStreamChunk,
                    ProduceChunks: token => streamingExecutor.ExecuteStreamingAsync(job, token),
                    ExecuteUnaryFallback: token => streamingExecutor.ExecuteAsync(job, token),
                    SendStream: (content, token) => cloud.StreamEngineeringAsync(job.JobId, content, token),
                    ReportFallbackResult: (result, token) => cloud.ReportEngineeringResultAsync(job.JobId, result, token),
                    StampChunk: StampChunkMachineId,
                    StampResult: StampResultMachineId,
                    GetChunkResult: static chunk => chunk.Result,
                    GetChunkError: static chunk => chunk.Error,
                    IsChunkDone: static chunk => chunk.Done,
                    CreateDoneChunk: static (result, error) => new EngineeringStreamChunk(Done: true, Error: error, Result: result),
                    CreateErrorResult: message => CreateErrorResponse(job.RunId, message),
                    CreateSynthesizedDoneResult: capturedError => CreateErrorResponse(
                        job.RunId,
                        capturedError ?? "Streaming engineering execution completed without a terminal result."),
                    CreateMissingFallbackResult: capturedError => CreateErrorResponse(
                        job.RunId,
                        capturedError ?? "Streaming engineering execution completed without a terminal result."),
                    FormatTimeoutMessage: () => $"Local Plane 2 engineering job exceeded {timeoutSeconds}s execution cap.",
                    CreateTimeoutFallbackResult: () => CreateErrorResponse(
                        job.RunId,
                        $"timeout: Local Plane 2 engineering job exceeded {timeoutSeconds}s execution cap.")),
                jobCt: jobCt,
                outerCt: ct).ConfigureAwait(false);
            return;
        }

        ConnectedEngineeringResponse response;
        try
        {
            response = StampResultMachineId(await localEngineeringExecutor.ExecuteAsync(job, jobCt).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Local Plane 2 engineering job {JobId} (run={RunId}) timed out after {TimeoutSeconds}s.", job.JobId, job.RunId, timeoutSeconds);
            response = CreateErrorResponse(job.RunId, $"timeout: Local Plane 2 engineering job exceeded {timeoutSeconds}s execution cap.");
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Local Plane 2 engineering job {JobId} (run={RunId}) failed with {ErrorType}.", job.JobId, job.RunId, error.GetType().Name);
            response = CreateErrorResponse(job.RunId, error.Message);
        }

        await cloud.ReportEngineeringResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private ConnectedEngineeringResponse CreateErrorResponse(string runId, string error)
        => new(
            Ok: false,
            RunId: runId,
            Error: error,
            ExecutedByMachineId: thisMachine.Value);

    private ConnectedEngineeringResponse StampResultMachineId(ConnectedEngineeringResponse response)
        => string.IsNullOrWhiteSpace(response.ExecutedByMachineId)
            ? response with { ExecutedByMachineId = thisMachine.Value }
            : response;

    private EngineeringStreamChunk StampChunkMachineId(EngineeringStreamChunk chunk)
    {
        if (chunk.Result is null || !string.IsNullOrWhiteSpace(chunk.Result.ExecutedByMachineId))
        {
            return chunk;
        }

        return chunk with
        {
            Result = chunk.Result with { ExecutedByMachineId = thisMachine.Value },
        };
    }
}
