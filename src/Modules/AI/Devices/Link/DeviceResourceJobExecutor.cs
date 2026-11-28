using DigitalBrain.Identity.Local.Machine;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Devices.Link;

// Executes Mode B PC resource jobs (IWindowsFolderGateway / IConsole) with fail-closed consent and [PersonOnly] approval checks.
internal sealed class DeviceResourceJobExecutor(
    CloudClient cloud,
    ILocalPcResourceExecutor localResourceExecutor,
    DeviceCapabilityReporter capabilities,
    MachineId thisMachine,
    ILogger logger)
{
    public async Task ExecuteAsync(PcResourceRequest job, CancellationToken ct)
    {
        var isConsole = string.Equals(job.Capability, ClientComputeConsentPolicy.ConsoleCapability, StringComparison.Ordinal);
        var isFolder = string.Equals(job.Capability, ClientComputeConsentPolicy.WindowsFolderGatewayCapability, StringComparison.Ordinal);
        var allowFolders = capabilities.EffectiveAllowWindowsFolderGateway();
        var allowConsole = capabilities.EffectiveAllowConsole();

        if (!isConsole && !isFolder)
        {
            logger.LogInformation("Refused Mode B resource job {JobId}: unsupported capability '{Capability}'.", job.JobId, job.Capability);
            await ReportErrorAsync(job.JobId, $"unsupported-capability: '{job.Capability}' is not supported on this PC.", ct).ConfigureAwait(false);
            return;
        }

        if (isFolder && !allowFolders)
        {
            logger.LogInformation("Refused Mode B folder job {JobId}: IWindowsFolderGateway consent is OFF.", job.JobId);
            await ReportErrorAsync(job.JobId, "consent-disabled: IWindowsFolderGateway is disabled on this PC.", ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && !allowConsole)
        {
            logger.LogInformation("Refused Mode B console job {JobId}: IConsole consent is OFF.", job.JobId);
            await ReportErrorAsync(job.JobId, "consent-disabled: IConsole is disabled on this PC.", ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && string.IsNullOrWhiteSpace(job.ApprovalToken))
        {
            logger.LogInformation("Refused Mode B console job {JobId}: missing [PersonOnly] approval proof.", job.JobId);
            await ReportErrorAsync(job.JobId, "person-approval-required: IConsole requires [PersonOnly] human approval proof.", ct).ConfigureAwait(false);
            return;
        }

        PcResourceResponse response;
        try
        {
            response = await localResourceExecutor.ExecuteAsync(job, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Mode B local PC resource job {JobId} ({Capability}/{Operation}) failed with {ErrorType}.", job.JobId, job.Capability, job.Operation, error.GetType().Name);
            response = new PcResourceResponse(
                Ok: false,
                Error: error.Message,
                ExecutedByMachineId: thisMachine.Value);
        }

        await cloud.ReportResourceResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private Task ReportErrorAsync(string jobId, string error, CancellationToken ct)
        => cloud.ReportResourceResultAsync(
            jobId,
            new PcResourceResponse(
                Ok: false,
                Error: error,
                ExecutedByMachineId: thisMachine.Value),
            ct);
}
