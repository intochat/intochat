using DigitalBrain.Identity.Local.Machine;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

// Executes Plane 1 connected sandbox jobs with fail-closed consent checks and NDJSON streaming + unary fallback.
internal sealed class DeviceSandboxJobExecutor(
    CloudClient cloud,
    ILocalSandboxExecutor localSandbox,
    DeviceCapabilityReporter capabilities,
    MachineId thisMachine,
    ILogger logger)
{
    public async Task ExecuteAsync(ConnectedSandboxRequest job, CancellationToken ct)
    {
        var compute = capabilities.CurrentComputeOptions();
        var allowClientCodeSandbox = capabilities.EffectiveAllowClientCodeSandbox(compute);
        if (!compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox))
        {
            logger.LogInformation(
                "Refused connected sandbox job {JobId}: local compute consent mode is {ConsentMode} (AllowClientCodeSandbox={AllowClientCodeSandbox}).",
                job.JobId,
                compute.ConsentMode,
                allowClientCodeSandbox);
            await cloud.ReportSandboxResultAsync(
                job.JobId,
                CreateErrorResponse("consent-disabled"),
                ct).ConfigureAwait(false);
            return;
        }

        if (localSandbox is IStreamingLocalSandboxExecutor streamingSandbox)
        {
            await NdjsonJobStreamer.ExecuteAsync(
                new NdjsonStreamRequest<SandboxStreamChunk, ConnectedSandboxResponse>(
                    ChunkTypeInfo: DeviceWireJson.Default.SandboxStreamChunk,
                    ProduceChunks: token => streamingSandbox.ExecuteStreamingAsync(job, token),
                    ExecuteUnaryFallback: token => streamingSandbox.ExecuteAsync(job, token),
                    SendStream: (content, token) => cloud.StreamSandboxAsync(job.JobId, content, token),
                    ReportFallbackResult: (result, token) => cloud.ReportSandboxResultAsync(job.JobId, result, token),
                    StampChunk: StampChunkMachineId,
                    StampResult: StampResultMachineId,
                    GetChunkResult: static chunk => chunk.Result,
                    GetChunkError: static chunk => chunk.Error,
                    IsChunkDone: static chunk => chunk.Done,
                    CreateDoneChunk: static (result, error) => new SandboxStreamChunk(Done: true, Error: error, Result: result),
                    CreateErrorResult: CreateErrorResponse,
                    CreateSynthesizedDoneResult: _ => new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: null,
                        UpdatedStateJson: null,
                        Error: null,
                        ExecutedByMachineId: thisMachine.Value),
                    CreateMissingFallbackResult: capturedError => CreateErrorResponse(
                        capturedError ?? "Streaming sandbox execution completed without a terminal result."),
                    OnStreamError: error => logger.LogWarning(error, "Streaming local sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name),
                    OnFallbackError: error => logger.LogWarning(error, "Local UnitHost sandbox job {JobId} fallback failed with {ErrorType}.", job.JobId, error.GetType().Name)),
                jobCt: ct,
                outerCt: ct).ConfigureAwait(false);
            return;
        }

        ConnectedSandboxResponse response;
        try
        {
            response = StampResultMachineId(await localSandbox.ExecuteAsync(job, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Local UnitHost sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
            response = CreateErrorResponse(error.Message);
        }

        await cloud.ReportSandboxResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private ConnectedSandboxResponse CreateErrorResponse(string error)
        => new(
            Handled: false,
            ResultJson: null,
            UpdatedStateJson: null,
            Error: error,
            ExecutedByMachineId: thisMachine.Value);

    private ConnectedSandboxResponse StampResultMachineId(ConnectedSandboxResponse response)
        => string.IsNullOrWhiteSpace(response.ExecutedByMachineId)
            ? response with { ExecutedByMachineId = thisMachine.Value }
            : response;

    private SandboxStreamChunk StampChunkMachineId(SandboxStreamChunk chunk)
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
